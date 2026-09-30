import { Component, computed, inject, signal } from '@angular/core';
import { SignalRService } from '../../../core/services/signalr.service';
import { FeedType } from '../../../shared/models/api.models';
import { VideoFeedComponent } from '../video-feed/video-feed.component';

/** Grid of all camera feeds with a CCTV/Drone filter, heatmap toggle and click-to-focus. */
@Component({
  selector: 'app-video-wall',
  imports: [VideoFeedComponent],
  template: `
    <section class="panel flex flex-col" aria-labelledby="feeds-title">
      <header class="panel-head flex-wrap">
        <h2 id="feeds-title" class="panel-title">Live Feeds</h2>
        <div class="seg" role="tablist" aria-label="Feed filter">
          @for (f of filters; track f) {
            <button type="button" role="tab" class="seg-btn" [class.seg-btn-active]="filter() === f"
                    [attr.aria-selected]="filter() === f" (click)="filter.set(f)">{{ f === 'all' ? 'All' : f }}</button>
          }
        </div>
        <label class="flex items-center gap-2 text-xs text-slate-400">
          <input type="checkbox" class="h-4 w-4 accent-emerald-500" [checked]="heatmap()" (change)="heatmap.set(!heatmap())" /> Heatmap
        </label>
      </header>

      @if (!live.loaded()) {
        <p class="p-6 text-center text-sm text-slate-500">Loading camera feeds…</p>
      } @else if (!visible().length) {
        <p class="p-6 text-center text-sm text-slate-500">No camera feeds registered.</p>
      } @else {
        <div class="grid flex-1 content-start gap-2.5 p-3"
             [class]="focused() ? 'grid-cols-1' : 'grid-cols-[repeat(auto-fill,minmax(min(100%,360px),1fr))]'">
          @for (cam of visible(); track cam.id) {
            <app-video-feed [camera]="cam" [showHeatmap]="heatmap()" [focused]="focused() === cam.id"
                            (toggleFocus)="toggleFocus($event)" />
          }
        </div>
      }
    </section>
  `,
})
export class VideoWallComponent {
  protected readonly live = inject(SignalRService);
  protected readonly filters: ('all' | FeedType)[] = ['all', 'CCTV', 'Drone'];
  protected readonly filter = signal<'all' | FeedType>('all');
  protected readonly heatmap = signal(true);
  protected readonly focused = signal<string | null>(null);

  protected readonly visible = computed(() => {
    const f = this.filter();
    const focus = this.focused();
    return this.live.cameras().filter((c) => (f === 'all' || c.feedType === f) && (!focus || c.id === focus));
  });

  protected toggleFocus(id: string): void {
    this.focused.update((current) => (current === id ? null : id));
  }
}
