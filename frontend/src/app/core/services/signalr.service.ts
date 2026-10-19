import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { HttpTransportType, HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  ActivityEntry, CameraFeed, DashboardSnapshot, DetectionFrame, IncidentAlert, IncidentEvent, IncidentLog,
  NotificationResult, SensorNode, SmartSprinkler,
} from '../../shared/models/api.models';
import { SEVERITY_RANK, systemLevel } from '../../shared/utils/format';
import { AuthService } from './auth.service';
import { PushNotificationService } from './push-notification.service';
import { AlarmService, ToastService } from './ui.service';

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/** Lightweight per-camera summary used by tiles and KPIs (the full frame goes to the canvas via detection$). */
export interface FrameStats {
  cameraId: string; humanCount: number; dangerCount: number;
  fireDetected: boolean; fireSeverity: string | null; simulated: boolean; receivedAt: number;
  smokeDetected: boolean; model: string | null;
}

export const HISTORY_POINTS = 60;
const MAX_INCIDENTS = 200;
const MAX_ACTIVITY = 250;
// The real AI service sends ~2 frames/s for one camera and ~0.6/s each for three (measured), slower on a busy CPU:
// 1.5 s marked live cameras "NO AI DATA". The backend raises "camera lost" only after 10 s.
export const FRAME_STALE_MS = 4000;

/**
 * Owns the SignalR connection to /hubs/monitoring and the live dashboard state.
 * Components read the signals; canvas overlays subscribe to detection$ (stream-rate data).
 */
@Injectable({ providedIn: 'root' })
export class SignalRService {
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly alarm = inject(AlarmService);
  private readonly push = inject(PushNotificationService);

  private connection: HubConnection | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private retryAttempt = 0;
  private pendingStats = new Map<string, FrameStats>();

  // ---------------------------------------------------------------- state
  readonly state = signal<ConnectionState>('disconnected');
  /** true once the first snapshot has arrived (drives loading states). */
  readonly loaded = signal(false);
  readonly cameras = signal<CameraFeed[]>([]);
  readonly sensors = signal<SensorNode[]>([]);
  readonly sprinklers = signal<SmartSprinkler[]>([]);
  readonly incidents = signal<IncidentAlert[]>([]);
  readonly activity = signal<ActivityEntry[]>([]);
  readonly frameStats = signal<Record<string, FrameStats>>({});
  /** 1-second clock tick: drives the header clock, relative times and stale-feed detection. */
  readonly now = signal(Date.now());

  /** Every validated AI frame, at stream rate. */
  readonly detection$ = new Subject<DetectionFrame>();
  /** New history-log entries (incident lifecycle, fire measurements, person-in-danger, route estimates). */
  readonly incidentLog$ = new Subject<IncidentLog>();

  readonly openIncidents = computed(() => this.incidents().filter((i) => !i.isResolved));
  readonly level = computed(() => systemLevel(this.openIncidents().map((i) => i.severity), this.sensors().map((s) => s.state)));
  readonly zones = computed(() =>
    [...new Set([...this.cameras().map((c) => c.locationZone), ...this.sensors().map((s) => s.locationZone), ...this.sprinklers().map((s) => s.zone)])].sort());
  readonly liveStats = computed(() => {
    const now = this.now();
    return Object.values(this.frameStats()).filter((s) => now - s.receivedAt < FRAME_STALE_MS);
  });

  constructor() {
    setInterval(() => this.now.set(Date.now()), 1000);
    // The alarm rings while any incident is open; resolving the last one (or the mute button) silences it.
    effect(() => {
      const open = this.openIncidents();
      if (open.length) this.alarm.start(open.some((i) => i.severity === 'Critical'));
      else this.alarm.stop();
    });
    // Coalesce per-camera stats: frames arrive ~20/s in total, the UI only needs ~4 updates/s.
    setInterval(() => {
      if (!this.pendingStats.size) return;
      const batch = Object.fromEntries(this.pendingStats);
      this.pendingStats.clear();
      this.frameStats.update((s) => ({ ...s, ...batch }));
    }, 250);
  }

  // ---------------------------------------------------------------- lifecycle
  start(): void {
    if (this.connection) return;
    const connection = new HubConnectionBuilder()
      .withUrl(environment.hubUrl, {
        accessTokenFactory: () => this.auth.token() ?? '',
        transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30000, 1000 * 2 ** ctx.previousRetryCount) + Math.random() * 500 })
      .configureLogging(environment.production ? LogLevel.Warning : LogLevel.Information)
      .build();
    this.connection = connection;

    connection.on('snapshot', (s: DashboardSnapshot) => this.applySnapshot(s));
    connection.on('detection', (f: DetectionFrame) => this.onDetection(f));
    connection.on('telemetry', (s: SensorNode) => this.upsertSensor(s));
    connection.on('sprinkler', (s: SmartSprinkler) => this.upsertSprinkler(s));
    connection.on('incident', (e: IncidentEvent) => this.onIncident(e));
    connection.on('activity', (a: ActivityEntry) => this.addActivity(a));
    connection.on('notification', (n: NotificationResult) => this.onNotification(n));
    connection.on('incidentLog', (l: IncidentLog) => this.incidentLog$.next(l));

    connection.onreconnecting(() => this.state.set('reconnecting'));
    connection.onreconnected(async () => {
      this.state.set('connected');
      this.addActivity({ level: 'success', message: 'Realtime link re-established', zone: null, timestamp: new Date().toISOString() });
      try { this.applySnapshot(await connection.invoke<DashboardSnapshot>('GetSnapshot')); } catch { /* next event will resync */ }
    });
    connection.onclose(() => {
      this.state.set('disconnected');
      if (this.connection === connection) this.scheduleRetry();
    });

    this.connect();
  }

  async stop(): Promise<void> {
    clearTimeout(this.retryTimer);
    const c = this.connection;
    this.connection = null;
    this.state.set('disconnected');
    this.loaded.set(false);
    await c?.stop();
  }

  private async connect(): Promise<void> {
    const c = this.connection;
    if (!c || c.state !== HubConnectionState.Disconnected) return;
    this.state.set('connecting');
    try {
      await c.start();
      this.retryAttempt = 0;
      this.state.set('connected');
      this.addActivity({ level: 'success', message: 'Realtime link established', zone: null, timestamp: new Date().toISOString() });
    } catch (err) {
      this.state.set('disconnected');
      if (String(err).includes('401')) { this.auth.logout(); return; }   // token expired / invalid
      this.scheduleRetry();
    }
  }

  private scheduleRetry(): void {
    clearTimeout(this.retryTimer);
    const delay = Math.min(30000, 1000 * 2 ** this.retryAttempt++);
    this.retryTimer = setTimeout(() => this.connect(), delay);
  }

  // ---------------------------------------------------------------- state updates (also used after REST calls)
  applySnapshot(s: DashboardSnapshot): void {
    this.loaded.set(true);
    this.cameras.set(s.cameras);
    this.sensors.set(s.sensors.map((x) => ({ ...x, history: (x.history ?? []).slice(-HISTORY_POINTS) })));
    this.sprinklers.set(s.sprinklers);
    this.incidents.set(s.incidents);
  }

  upsertSensor(sensor: SensorNode): void {
    this.sensors.update((list) => {
      const prev = list.find((s) => s.id === sensor.id);
      const history = [...(prev?.history ?? [])];
      if (!prev || prev.lastReadingAt !== sensor.lastReadingAt) history.push(sensor.currentValue);
      const next = { ...sensor, history: history.slice(-HISTORY_POINTS) };
      return prev ? list.map((s) => (s.id === sensor.id ? next : s)) : [...list, next];
    });
  }

  upsertSprinkler(sprinkler: SmartSprinkler): void {
    this.sprinklers.update((list) =>
      list.some((s) => s.id === sprinkler.id) ? list.map((s) => (s.id === sprinkler.id ? sprinkler : s)) : [...list, sprinkler]);
  }

  upsertIncident(incident: IncidentAlert): void {
    this.incidents.update((list) => {
      const next = list.some((i) => i.id === incident.id) ? list.map((i) => (i.id === incident.id ? incident : i)) : [incident, ...list];
      next.sort((a, b) => b.timestamp.localeCompare(a.timestamp));
      while (next.length > MAX_INCIDENTS) {
        const idx = next.map((i) => i.isResolved).lastIndexOf(true);
        if (idx < 0) break;
        next.splice(idx, 1);
      }
      return next;
    });
  }

  addActivity(entry: ActivityEntry): void {
    this.activity.update((list) => [entry, ...list].slice(0, MAX_ACTIVITY));
  }

  clearActivity(): void {
    this.activity.set([]);
  }

  // ---------------------------------------------------------------- event handlers
  private onDetection(frame: DetectionFrame): void {
    this.detection$.next(frame);
    this.pendingStats.set(frame.cameraId, {
      cameraId: frame.cameraId, humanCount: frame.humanCount, dangerCount: frame.dangerCount,
      fireDetected: frame.fireDetected, fireSeverity: frame.fireSeverity, simulated: frame.simulated, receivedAt: Date.now(),
      smokeDetected: frame.smokeDetected, model: frame.model,
    });
  }

  private onIncident({ action, incident }: IncidentEvent): void {
    this.upsertIncident(incident);
    if (action !== 'created' && action !== 'escalated') return;
    const title = `${incident.severity.toUpperCase()} — Incident #${incident.id}`;
    if (SEVERITY_RANK[incident.severity] >= SEVERITY_RANK.High) {
      this.toast.show(title, incident.message, 'critical', 10000);
      this.push.notifyLocal(incident);
    } else {
      this.toast.show(title, incident.message, 'warning');
    }
  }

  private onNotification(n: NotificationResult): void {
    const channel = n.channel.toUpperCase();
    const message = n.skipped
      ? `${channel} skipped for incident #${n.incidentId} (${n.detail})`
      : n.ok
        ? `${channel} alert for incident #${n.incidentId} delivered to ${n.delivered}${n.detail ? ` — ${n.detail}` : ''}`
        : `${channel} alert for incident #${n.incidentId} failed${n.detail ? `: ${n.detail}` : ''}`;
    this.addActivity({ level: n.skipped ? 'info' : n.ok ? 'notify' : 'warning', message, zone: null, timestamp: n.timestamp });
  }
}
