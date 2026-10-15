import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { IncidentLog, IncidentLogType, SensorHistory } from '../../../shared/models/api.models';
import { errorMessage } from '../../../shared/utils/format';

export interface ChartPoint { t: number; v: number; }
const W = 600, H = 140;

/** SVG polyline for a time series scaled into a W×H box. Null when there is nothing to draw. */
export function chartPath(points: ChartPoint[]): { line: string; max: number; from: number; to: number } | null {
  if (!points.length) return null;
  const from = points[0].t, to = points[points.length - 1].t;
  const max = Math.max(...points.map((p) => p.v)) * 1.1 || 1;
  const span = to - from || 1;
  const line = points.map((p) => `${(((p.t - from) / span) * W).toFixed(1)},${(H - (p.v / max) * H).toFixed(1)}`).join(' ');
  return { line, max, from, to };
}

/**
 * Requirements 3.2 + 5.3: per-incident history (opened/escalated/resolved, fire measurements, person-in-danger,
 * evacuation-route estimates) with filters, live updates over SignalR, and a growth graph of the measurements
 * that actually exist: AI fire-box area for camera incidents, sensor readings for sensor incidents.
 */
@Component({
  selector: 'app-incident-history',
  imports: [DatePipe],
  templateUrl: './incident-history.component.html',
})
export class IncidentHistoryComponent {
  private readonly api = inject(MonitoringApiService);
  protected readonly live = inject(SignalRService);

  protected readonly types: IncidentLogType[] = ['Opened', 'Escalated', 'Resolved', 'FireMeasurement', 'PersonInDanger', 'EvacuationRoute'];
  protected readonly W = W;
  protected readonly H = H;
  protected readonly incidentId = signal<number | null>(null);
  protected readonly type = signal<IncidentLogType | ''>('');
  protected readonly logs = signal<IncidentLog[]>([]);
  protected readonly sensor = signal<SensorHistory | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly incident = computed(() => this.live.incidents().find((i) => i.id === this.incidentId()) ?? null);
  protected readonly series = computed(() => {
    const inc = this.incident();
    if (!inc) return null;
    if (inc.sensorId && this.sensor()) {
      const s = this.sensor()!;
      return { label: `Sensor readings: ${s.sensorId} (${s.unit})`, unit: s.unit,
        chart: chartPath(s.readings.map((r) => ({ t: Date.parse(r.recordedAt), v: r.value }))) };
    }
    const m = this.logs().filter((l) => l.type === 'FireMeasurement' && l.fireAreaPercent !== null).reverse();
    return { label: 'AI fire bounding-box area (% of camera frame) — not a physical fire size', unit: '%',
      chart: chartPath(m.map((l) => ({ t: Date.parse(l.timestamp), v: l.fireAreaPercent! }))) };
  });

  constructor() {
    effect(() => { const id = this.incidentId(), t = this.type(); untracked(() => this.load(id, t)); });
    this.live.incidentLog$.pipe(takeUntilDestroyed()).subscribe((l) => {
      const id = this.incidentId(), t = this.type();
      if ((id === null || l.incidentId === id) && (!t || l.type === t)) this.logs.update((list) => [l, ...list].slice(0, 500));
    });
  }

  private load(id: number | null, type: IncidentLogType | ''): void {
    this.loading.set(true);
    this.api.incidentLogs(id ?? undefined, type || undefined).subscribe({
      next: (logs) => {
        if (this.incidentId() !== id || this.type() !== type) return; // a newer filter is already loading
        this.logs.set(logs); this.loading.set(false); this.error.set(null);
      },
      error: (err) => {
        if (this.incidentId() !== id || this.type() !== type) return;
        this.loading.set(false); this.error.set(errorMessage(err));
      },
    });
    this.sensor.set(null);
    const inc = untracked(() => this.incident());
    if (inc?.sensorId) this.api.sensorHistory(inc.sensorId, inc.timestamp).subscribe({ next: (s) => { if (this.incidentId() === id) this.sensor.set(s); }, error: () => undefined });
  }

  protected selectIncident(value: string): void {
    this.incidentId.set(value ? Number(value) : null);
  }
}
