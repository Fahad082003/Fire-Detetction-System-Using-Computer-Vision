import { Component, computed, inject, signal } from '@angular/core';
import { MonitoringApiService } from '../../core/services/monitoring-api.service';
import { SignalRService } from '../../core/services/signalr.service';
import { ConfirmService, ToastService } from '../../core/services/ui.service';
import { AuthService } from '../../core/services/auth.service';
import { CameraFeed, FeedType, SaveCamera, StreamFormat } from '../../shared/models/api.models';
import { errorMessage } from '../../shared/utils/format';

const EMPTY: SaveCamera = { id: '', name: '', feedType: 'CCTV', streamUrl: '', streamFormat: 'Simulated', locationZone: '' };

/**
 * Admin device setup: a customer's own cameras (name, zone, stream link) instead of the demo list.
 * Zones are picked from the ones cameras, sensors and sprinklers already use, so a typo cannot split a zone.
 */
@Component({
  selector: 'app-devices',
  templateUrl: './devices.component.html',
})
export class DevicesComponent {
  private readonly api = inject(MonitoringApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly live = inject(SignalRService);
  private readonly auth = inject(AuthService);
  protected readonly isAdmin = computed(() => this.auth.user()?.role === 'Admin');

  protected readonly feedTypes: FeedType[] = ['CCTV', 'Drone'];
  protected readonly formats: StreamFormat[] = ['Simulated', 'Mjpeg', 'Hls', 'Mp4'];
  /** null = form closed, '' = adding a new camera, otherwise the id being edited. */
  protected readonly editing = signal<string | null>(null);
  protected readonly form = signal<SaveCamera>({ ...EMPTY });
  protected readonly busy = signal(false);

  protected add(): void {
    this.form.set({ ...EMPTY });
    this.editing.set('');
  }

  protected edit(c: CameraFeed): void {
    this.form.set({ id: c.id, name: c.name, feedType: c.feedType, streamUrl: c.streamUrl, streamFormat: c.streamFormat, locationZone: c.locationZone });
    this.editing.set(c.id);
  }

  protected set<K extends keyof SaveCamera>(key: K, event: Event): void {
    const value = (event.target as HTMLInputElement | HTMLSelectElement).value as SaveCamera[K];
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  protected save(): void {
    const id = this.editing();
    if (id === null) return;
    this.busy.set(true);
    const call = id === '' ? this.api.createCamera(this.form()) : this.api.updateCamera(id, this.form());
    call.subscribe({
      next: (saved) => {
        this.busy.set(false);
        this.editing.set(null);
        // shortcut: only this browser's list is updated; other open dashboards see the change after a reload.
        this.live.cameras.update((list) => (id === '' ? [...list, saved] : list.map((c) => (c.id === saved.id ? saved : c))));
        this.toast.show('Camera saved', `${saved.name} (${saved.locationZone})`, 'success', 3000);
      },
      error: (err) => { this.busy.set(false); this.toast.show('Camera not saved', errorMessage(err), 'error'); },
    });
  }

  protected async remove(c: CameraFeed): Promise<void> {
    if (!(await this.confirm.ask('Delete camera?', `"${c.name}" is removed. Its old incidents stay in the history.`, 'Delete'))) return;
    this.api.deleteCamera(c.id).subscribe({
      next: () => { this.live.cameras.update((list) => list.filter((x) => x.id !== c.id)); this.toast.show('Camera deleted', c.name, 'success', 3000); },
      error: (err) => this.toast.show('Camera not deleted', errorMessage(err), 'error'),
    });
  }
}
