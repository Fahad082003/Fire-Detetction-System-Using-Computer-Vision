import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { ToastService } from '../../../core/services/ui.service';
import { IncidentAlert } from '../../../shared/models/api.models';
import { errorMessage } from '../../../shared/utils/format';

const SOURCE_LABEL = { Vision: 'AI Vision', Sensor: 'Sensor', Manual: 'Manual' } as const;

/** Real-time incident register. Rows arrive over SignalR; "Resolve" calls POST /api/incidents/{id}/resolve. */
@Component({
  selector: 'app-alert-logs',
  imports: [DatePipe],
  templateUrl: './alert-logs.component.html',
})
export class AlertLogsComponent {
  protected readonly live = inject(SignalRService);
  private readonly api = inject(MonitoringApiService);
  private readonly toast = inject(ToastService);

  protected readonly filter = signal<'open' | 'all'>('open');
  protected readonly resolving = signal<ReadonlySet<number>>(new Set());
  protected readonly sourceLabel = SOURCE_LABEL;
  protected readonly rows = computed(() =>
    this.filter() === 'open' ? this.live.openIncidents() : this.live.incidents());

  protected location(i: IncidentAlert): string {
    return i.cameraName ? `${i.zone} (${i.cameraName})` : i.sensorId ? `${i.zone} (sensor ${i.sensorId})` : i.zone;
  }

  protected resolve(i: IncidentAlert): void {
    this.resolving.update((s) => new Set(s).add(i.id));
    const done = () => this.resolving.update((s) => { const n = new Set(s); n.delete(i.id); return n; });
    this.api.resolveIncident(i.id).subscribe({
      next: (updated) => { this.live.upsertIncident(updated); done(); },
      error: (err) => { this.toast.show('Could not resolve incident', errorMessage(err), 'error'); done(); },
    });
  }
}
