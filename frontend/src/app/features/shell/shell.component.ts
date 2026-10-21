import { DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';
import { MonitoringApiService } from '../../core/services/monitoring-api.service';
import { PushNotificationService } from '../../core/services/push-notification.service';
import { SignalRService } from '../../core/services/signalr.service';
import { AlarmService } from '../../core/services/ui.service';
import { AiStatus } from '../../shared/models/api.models';
import { DispatchDialogComponent } from '../dashboard/dispatch-dialog/dispatch-dialog.component';

/** AI health (models + per-camera stream state) is a cheap status call: 10 s keeps the banner within ~10 s of a camera failure. */
export const AI_POLL_MS = 10_000;
/** A camera whose AI frames stopped 8-120 s ago marks monitoring degraded before the next poll or the 10 s camera-lost alarm
 * (normal gaps measured up to 5.4 s at 3-4 cameras). Longer silences are left to the AI poll and the alarm. */
export const FRAMES_STALE_MS = 8_000;
const FRAMES_FORGET_MS = 120_000;

const LEVEL_STYLE = {
  nominal: 'border-emerald-500/40 bg-emerald-500/10 text-emerald-400',
  elevated: 'border-amber-500/40 bg-amber-500/10 text-amber-400',
  critical: 'border-red-500/60 bg-red-500/15 text-red-400 animate-pulse',
} as const;

/** Authenticated layout: top command bar + routed page. Owns the SignalR connection lifetime. */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, DatePipe, DispatchDialogComponent],
  templateUrl: './shell.component.html',
})
export class ShellComponent implements OnInit, OnDestroy {
  protected readonly live = inject(SignalRService);
  protected readonly auth = inject(AuthService);
  protected readonly alarm = inject(AlarmService);
  protected readonly push = inject(PushNotificationService);
  protected readonly dispatchDialog = viewChild.required(DispatchDialogComponent);
  private readonly api = inject(MonitoringApiService);
  private aiTimer?: ReturnType<typeof setInterval>;
  private aiPoll = 0;

  /** AI inference service health (null = status could not be read). Red = offline, amber = a model is missing. */
  protected readonly ai = signal<AiStatus | null>(null);
  protected readonly aiDot = computed(() => {
    const s = this.ai();
    return !s?.online ? 'bg-red-500' : s.service?.status === 'ok' ? 'bg-emerald-500' : 'bg-amber-500';
  });
  protected readonly aiTitle = computed(() => {
    const s = this.ai();
    if (!s) return 'AI service status unknown';
    if (!s.online) return `AI service offline: ${s.error ?? ''}`;
    const missing = s.service?.models.filter((m) => !m.loaded).map((m) => m.name) ?? [];
    return missing.length ? `AI online, missing model(s): ${missing.join(', ')}` : 'AI online, all models loaded';
  });

  /** Why "all clear" cannot be trusted right now (no AI = no detections, which is not the same as no fire), or null. */
  protected readonly degraded = computed(() => {
    if (this.live.state() !== 'connected') return 'LIVE LINK DOWN';
    const s = this.ai();
    if (!s) return 'AI STATUS UNKNOWN';
    if (!s.online) return 'AI OFFLINE';
    if (s.service?.status !== 'ok') return 'AI MODEL MISSING';
    // The API's "camera lost" alarm only watches cameras seen since it started; the AI service knows every stream it cannot read.
    if (s.service.cameras.some((c) => c.state === 'error' || c.state === 'reconnecting')) return 'CAMERA OFFLINE';
    // Between polls: AI frames that stopped arriving (e.g. the AI service just died) are known here first.
    const now = this.live.now();
    const cameras = new Set(this.live.cameras().map((c) => c.id));
    return Object.values(this.live.frameStats()).some((f) => cameras.has(f.cameraId)
      && now - f.receivedAt > FRAMES_STALE_MS && now - f.receivedAt < FRAMES_FORGET_MS) ? 'AI DATA STALE' : null;
  });
  protected readonly levelClass = computed(() =>
    LEVEL_STYLE[this.live.level() === 'nominal' && this.degraded() ? 'elevated' : this.live.level()]);
  protected readonly levelLabel = computed(() => {
    const open = this.live.openIncidents().length;
    const reason = this.degraded();
    const also = reason ? ` · ${reason}` : ''; // open incidents stay first; the monitoring gap is still shown
    return { nominal: reason ? `MONITORING DEGRADED · ${reason}` : 'ALL SYSTEMS NOMINAL',
      elevated: `ELEVATED RISK · ${open} OPEN${also}`, critical: `CRITICAL ALERT · ${open} OPEN${also}` }[this.live.level()];
  });
  protected readonly connection = computed(() => ({
    connected: { label: 'Live', dot: 'bg-emerald-500 shadow-[0_0_8px] shadow-emerald-500' },
    connecting: { label: 'Connecting', dot: 'bg-amber-500 animate-blink' },
    reconnecting: { label: 'Reconnecting', dot: 'bg-amber-500 animate-blink' },
    disconnected: { label: 'Offline', dot: 'bg-red-500' },
  })[this.live.state()]);

  ngOnInit(): void {
    this.live.start();
    this.push.init();
    this.pollAi();
    this.aiTimer = setInterval(() => this.pollAi(), AI_POLL_MS);
  }

  ngOnDestroy(): void {
    clearInterval(this.aiTimer);
    this.live.stop();
  }

  private pollAi(): void {
    const poll = ++this.aiPoll; // a slow older reply must not overwrite a newer one
    this.api.aiStatus().subscribe({
      next: (s) => { if (poll === this.aiPoll) this.ai.set(s); },
      error: () => { if (poll === this.aiPoll) this.ai.set(null); },
    });
  }

  protected async logout(): Promise<void> {
    await this.live.stop();
    this.auth.logout();
  }
}
