import { Component, computed, inject } from '@angular/core';
import { FrameStats, SignalRService } from '../../../core/services/signalr.service';
import { CameraFeed, SensorNode } from '../../../shared/models/api.models';
import { SEVERITY_RANK } from '../../../shared/utils/format';

type Tone = 'ok' | 'warn' | 'danger' | 'neutral';
interface Kpi { label: string; value: string; hint: string; tone: Tone; }

const TONE: Record<Tone, string> = {
  ok: 'border-l-emerald-500', warn: 'border-l-amber-500 [&_.v]:text-amber-400',
  danger: 'border-l-red-500 [&_.v]:text-red-400', neutral: 'border-l-ink-600',
};

/**
 * People across feeds: cameras in the same zone usually see the same people, so take the largest count per zone
 * (2 people seen by 3 kitchen cameras = 2, not 6), then add the zones. Cameras with no known zone count on their own.
 */
export function peopleCount(stats: FrameStats[], cameras: CameraFeed[]): { humans: number; danger: number } {
  const zoneOf = new Map(cameras.map((c) => [c.id, c.locationZone]));
  const perZone = new Map<string, { humans: number; danger: number }>();
  for (const s of stats) {
    const key = zoneOf.get(s.cameraId) ?? `camera:${s.cameraId}`;
    const z = perZone.get(key) ?? { humans: 0, danger: 0 };
    perZone.set(key, { humans: Math.max(z.humans, s.humanCount), danger: Math.max(z.danger, s.dangerCount) });
  }
  let humans = 0, danger = 0;
  for (const z of perZone.values()) { humans += z.humans; danger += z.danger; }
  return { humans, danger };
}

/** Four headline numbers: open incidents, humans tracked by AI, peak temperature, peak smoke. */
@Component({
  selector: 'app-kpi-strip',
  template: `
    <section class="grid grid-cols-2 gap-2.5 lg:grid-cols-4 2xl:grid-cols-2" aria-label="Key metrics">
      @for (k of kpis(); track k.label) {
        <article class="flex min-w-0 flex-col gap-0.5 rounded-xl border border-l-4 border-ink-700 bg-ink-900 px-4 py-3 {{ tone(k.tone) }}">
          <span class="text-[11px] font-semibold uppercase tracking-wider text-slate-500">{{ k.label }}</span>
          <span class="v font-mono text-2xl font-bold tabular-nums">{{ k.value }}</span>
          <span class="truncate text-xs text-slate-500">{{ k.hint }}</span>
        </article>
      }
    </section>
  `,
})
export class KpiStripComponent {
  private readonly live = inject(SignalRService);

  protected readonly kpis = computed<Kpi[]>(() => {
    const open = this.live.openIncidents();
    const critical = open.filter((i) => i.severity === 'Critical').length;
    const top = Math.max(-1, ...open.map((i) => SEVERITY_RANK[i.severity]));
    const stats = this.live.liveStats();
    const { humans, danger } = peopleCount(stats, this.live.cameras());
    const temp = this.peak('Temperature');
    const smoke = this.peak('Smoke');
    return [
      { label: 'Open Incidents', value: String(open.length), hint: critical ? `${critical} critical` : open.length ? 'monitoring' : 'none open',
        tone: top >= SEVERITY_RANK.High ? 'danger' : top >= 0 ? 'warn' : 'ok' },
      { label: 'Humans Tracked', value: String(humans), hint: `${danger} in danger · ${stats.length} live feed(s)`,
        tone: danger > 0 ? 'danger' : humans > 0 ? 'ok' : 'neutral' },
      { label: 'Peak Temperature', value: temp ? `${temp.currentValue.toFixed(1)}°C` : '—', hint: temp ? `${temp.locationZone} · ${temp.id}` : 'no sensors', tone: this.sensorTone(temp) },
      { label: 'Peak Smoke', value: smoke ? `${Math.round(smoke.currentValue)} ppm` : '—', hint: smoke ? `${smoke.locationZone} · ${smoke.id}` : 'no sensors', tone: this.sensorTone(smoke) },
    ];
  });

  protected tone(t: Tone): string { return TONE[t]; }

  private peak(type: SensorNode['type']): SensorNode | null {
    return this.live.sensors().filter((s) => s.type === type)
      .reduce<SensorNode | null>((best, s) => (!best || s.currentValue > best.currentValue ? s : best), null);
  }

  private sensorTone(s: SensorNode | null): Tone {
    if (!s) return 'neutral';
    return s.state === 'Normal' ? 'ok' : s.state === 'Warning' ? 'warn' : 'danger';
  }
}
