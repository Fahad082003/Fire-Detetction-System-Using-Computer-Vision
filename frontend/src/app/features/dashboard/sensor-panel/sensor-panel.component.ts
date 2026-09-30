import { Component, computed, inject, signal } from '@angular/core';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { ConfirmService, ToastService } from '../../../core/services/ui.service';
import { SensorNode, SensorState, SmartSprinkler, SprinklerMode } from '../../../shared/models/api.models';
import { errorMessage, relativeTime } from '../../../shared/utils/format';
import { SparklineComponent } from './sparkline.component';

export const STATE_COLORS: Record<SensorState, string> = {
  Normal: '#10b981', Warning: '#f59e0b', Alarm: '#ef4444', Critical: '#ef4444',
};

/**
 * Live IoT indicators: a ring gauge + sparkline per Smoke/Temperature sensor, and the Smart Sprinkler
 * controls (per-relay ON/OFF switch, Auto/Manual mode, system-wide master switch).
 */
@Component({
  selector: 'app-sensor-panel',
  imports: [SparklineComponent],
  templateUrl: './sensor-panel.component.html',
  styleUrl: './sensor-panel.component.css',
})
export class SensorPanelComponent {
  protected readonly live = inject(SignalRService);
  private readonly api = inject(MonitoringApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  protected readonly pending = signal<ReadonlySet<string>>(new Set());
  protected readonly masterBusy = signal(false);
  protected readonly activeCount = computed(() => this.live.sprinklers().filter((s) => s.isActive).length);
  protected readonly allActive = computed(() => this.live.sprinklers().length > 0 && this.activeCount() === this.live.sprinklers().length);

  protected readonly color = (s: SensorNode) => STATE_COLORS[s.state];
  /** Gauge fills 0–100 % of a scale that ends 10 % above the critical threshold. */
  protected readonly percent = (s: SensorNode) => Math.max(0, Math.min(100, (s.currentValue / (s.criticalThreshold * 1.1)) * 100));
  protected readonly display = (s: SensorNode) => (s.type === 'Temperature' ? s.currentValue.toFixed(1) : Math.round(s.currentValue).toString());
  protected readonly lastTriggered = (s: SmartSprinkler) => {
    this.live.now(); // re-evaluate every second
    return s.isActive ? `spraying · ${relativeTime(s.lastTriggeredAt)}` : `last ${relativeTime(s.lastTriggeredAt)}`;
  };

  protected toggle(s: SmartSprinkler, event: Event): void {
    const input = event.target as HTMLInputElement;
    this.send(s, { isActive: input.checked }, () => (input.checked = s.isActive));
  }

  protected setMode(s: SmartSprinkler, mode: SprinklerMode): void {
    if (mode !== s.mode) this.send(s, { mode });
  }

  protected async master(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const activate = input.checked;
    const ok = await this.confirm.ask(
      activate ? 'Activate ALL sprinklers?' : 'Deactivate all sprinklers?',
      activate
        ? 'Every relay in every zone will open and discharge water, including relays in Manual mode.'
        : 'All relays will close. Only do this once the area is confirmed safe.',
      activate ? 'Activate all' : 'Deactivate all');
    if (!ok) { input.checked = this.allActive(); return; }

    this.masterBusy.set(true);
    this.api.masterSprinklers(activate).subscribe({
      next: (res) => {
        res.sprinklers.forEach((s) => this.live.upsertSprinkler(s));
        this.toast.show(activate ? 'All sprinklers activated' : 'All sprinklers deactivated', `${res.changed.length} relay(s) changed`, activate ? 'critical' : 'success');
        this.masterBusy.set(false);
      },
      error: (err) => {
        input.checked = this.allActive();
        this.toast.show('Master switch failed', errorMessage(err), 'error');
        this.masterBusy.set(false);
      },
    });
  }

  private send(s: SmartSprinkler, body: { isActive?: boolean; mode?: SprinklerMode }, revert?: () => void): void {
    this.pending.update((p) => new Set(p).add(s.id));
    const done = () => this.pending.update((p) => { const n = new Set(p); n.delete(s.id); return n; });
    this.api.toggleSprinkler(s.id, body).subscribe({
      next: (updated) => { this.live.upsertSprinkler(updated); done(); },
      error: (err) => {
        revert?.();
        this.toast.show(`Sprinkler ${s.id} command failed`, errorMessage(err), 'error');
        done();
      },
    });
  }
}
