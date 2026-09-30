import {
  AfterViewInit, Component, DestroyRef, ElementRef, computed, effect, inject, input, output, signal, viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import type Hls from 'hls.js';
import { AuthService } from '../../../core/services/auth.service';
import { MonitoringApiService } from '../../../core/services/monitoring-api.service';
import { SignalRService } from '../../../core/services/signalr.service';
import { CameraFeed } from '../../../shared/models/api.models';
import { OverlayLoopService, OverlayRenderer } from './overlay-renderer';

const MJPEG_RETRY_MS = 5000;

/**
 * One CCTV / drone feed: the video element (MJPEG <img>, HLS/MP4 <video>, or a simulated backdrop)
 * with an aligned <canvas> on top where the AI bounding boxes are drawn live.
 */
@Component({
  selector: 'app-video-feed',
  templateUrl: './video-feed.component.html',
  styleUrl: './video-feed.component.css',
})
export class VideoFeedComponent implements AfterViewInit {
  readonly camera = input.required<CameraFeed>();
  readonly showHeatmap = input(true);
  readonly focused = input(false);
  readonly toggleFocus = output<string>();

  private readonly live = inject(SignalRService);
  private readonly api = inject(MonitoringApiService);
  private readonly auth = inject(AuthService);
  private readonly loop = inject(OverlayLoopService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly stage = viewChild.required<ElementRef<HTMLElement>>('stage');
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly mediaEl = viewChild<ElementRef<HTMLVideoElement | HTMLImageElement>>('mediaEl');

  private renderer: OverlayRenderer | null = null;
  private hls: Hls | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;

  protected readonly stale = signal(true);
  protected readonly offline = signal(false);
  private readonly retry = signal(0);

  /** No URL means there is no real stream: show the simulated backdrop, still draw the AI overlay. */
  protected readonly format = computed(() => (this.camera().streamUrl ? this.camera().streamFormat : 'Simulated'));
  protected readonly mjpegUrl = computed(() => {
    const url = this.api.cameraStreamUrl(this.camera().id, this.auth.token());
    return this.retry() ? `${url}${url.includes('?') ? '&' : '?'}r=${this.retry()}` : url;
  });
  protected readonly stats = computed(() => (this.stale() ? null : this.live.frameStats()[this.camera().id] ?? null));
  protected readonly alertLevel = computed(() => {
    const s = this.stats();
    if (!s) return '';
    if (s.dangerCount > 0 || s.fireSeverity === 'Critical' || (s.fireDetected && s.humanCount > 0)) return 'critical';
    return s.fireDetected ? 'elevated' : '';
  });

  constructor() {
    effect(() => { if (this.renderer) this.renderer.showHeatmap = this.showHeatmap(); });
    // (Re)attach the media element whenever the stream type or URL changes.
    effect(() => {
      const el = this.mediaEl()?.nativeElement ?? null;
      const format = this.format();
      const url = this.camera().streamUrl;
      if (this.renderer) this.renderer.media = el;
      this.destroyHls();
      if (el instanceof HTMLVideoElement && format === 'Hls') this.attachHls(el, url);
    });
    this.destroyRef.onDestroy(() => {
      clearTimeout(this.retryTimer);
      this.destroyHls();
      if (this.renderer) { this.loop.unregister(this.renderer); this.renderer.destroy(); }
    });
  }

  ngAfterViewInit(): void {
    this.renderer = new OverlayRenderer(this.stage().nativeElement, this.canvas().nativeElement, (s) => this.stale.set(s));
    this.renderer.showHeatmap = this.showHeatmap();
    this.renderer.media = this.mediaEl()?.nativeElement ?? null;
    this.loop.register(this.renderer);

    this.live.detection$
      .pipe(filter((f) => f.cameraId === this.camera().id), takeUntilDestroyed(this.destroyRef))
      .subscribe((f) => this.renderer?.update(f));
  }

  protected onMjpegError(): void {
    this.offline.set(true);
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => this.retry.update((n) => n + 1), MJPEG_RETRY_MS);
  }

  private async attachHls(video: HTMLVideoElement, url: string): Promise<void> {
    if (video.canPlayType('application/vnd.apple.mpegurl')) {       // Safari: native HLS
      video.src = url;
      video.play().catch(() => undefined);
      return;
    }
    const { default: HlsCtor } = await import('hls.js');              // lazy chunk, only when an HLS feed exists
    if (!HlsCtor.isSupported()) { this.offline.set(true); return; }
    const hls = new HlsCtor({ lowLatencyMode: true, liveSyncDurationCount: 2, maxBufferLength: 10 });
    this.hls = hls;
    hls.on(HlsCtor.Events.ERROR, (_e, data) => {
      if (!data.fatal) return;
      this.offline.set(true);
      if (data.type === HlsCtor.ErrorTypes.NETWORK_ERROR) setTimeout(() => hls.startLoad(), 3000);
      else if (data.type === HlsCtor.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
      else this.destroyHls();
    });
    hls.on(HlsCtor.Events.MANIFEST_PARSED, () => { this.offline.set(false); video.play().catch(() => undefined); });
    hls.loadSource(url);
    hls.attachMedia(video);
  }

  private destroyHls(): void {
    this.hls?.destroy();
    this.hls = null;
  }
}
