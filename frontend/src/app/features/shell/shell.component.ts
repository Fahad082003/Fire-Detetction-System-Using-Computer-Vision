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

  protected readonly levelClass = computed(() => LEVEL_STYLE[this.live.level()]);
  protected readonly levelLabel = computed(() => {
    const open = this.live.openIncidents().length;
    return { nominal: 'ALL SYSTEMS NOMINAL', elevated: `ELEVATED RISK · ${open} OPEN`, critical: `CRITICAL ALERT · ${open} OPEN` }[this.live.level()];
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
    this.aiTimer = setInterval(() => this.pollAi(), 30_000);
  }

  ngOnDestroy(): void {
    clearInterval(this.aiTimer);
    this.live.stop();
  }

  private pollAi(): void {
    this.api.aiStatus().subscribe({ next: (s) => this.ai.set(s), error: () => this.ai.set(null) });
  }

  protected async logout(): Promise<void> {
    await this.live.stop();
    this.auth.logout();
  }
}
