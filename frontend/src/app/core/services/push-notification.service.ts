import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { IncidentAlert } from '../../shared/models/api.models';
import { errorMessage } from '../../shared/utils/format';
import { MonitoringApiService } from './monitoring-api.service';
import { ToastService } from './ui.service';

function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4);
  const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

/**
 * Web Push: with VAPID keys on the server, alerts reach this device through the service worker (public/sw.js)
 * even when the tab is closed. Without VAPID keys it falls back to local desktop notifications while the tab is open.
 */
@Injectable({ providedIn: 'root' })
export class PushNotificationService {
  private readonly api = inject(MonitoringApiService);
  private readonly toast = inject(ToastService);
  private registration: ServiceWorkerRegistration | null = null;
  private vapidKey: string | null = null;
  private subscription: PushSubscription | null = null;

  readonly supported = typeof navigator !== 'undefined' && 'serviceWorker' in navigator && 'Notification' in window;
  readonly enabled = signal(false);
  readonly mode = signal<'push' | 'local' | 'off'>('off');

  async init(): Promise<void> {
    if (!this.supported || this.registration) return;
    try {
      this.registration = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
      this.vapidKey = (await firstValueFrom(this.api.vapidPublicKey())).publicKey;
      if (this.vapidKey && 'PushManager' in window) this.subscription = await this.registration.pushManager.getSubscription();
      const local = !this.vapidKey && Notification.permission === 'granted';
      this.setMode(this.subscription ? 'push' : local ? 'local' : 'off');
    } catch (err) {
      console.warn('[push] init failed', err);
    }
  }

  async toggle(): Promise<void> {
    return this.enabled() ? this.disable() : this.enable();
  }

  private async enable(): Promise<void> {
    if (!this.supported) return;
    const permission = await Notification.requestPermission();
    if (permission !== 'granted') {
      this.toast.show('Notifications blocked', 'Allow notifications for this site in the browser settings.', 'warning');
      return;
    }
    try {
      if (this.vapidKey && this.registration && 'PushManager' in window) {
        this.subscription = await this.registration.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: urlBase64ToUint8Array(this.vapidKey),
        });
        await firstValueFrom(this.api.subscribePush(this.subscription.toJSON()));
        this.setMode('push');
        this.toast.show('Push alerts enabled', 'This device receives incident alerts even when the dashboard is closed.', 'success');
      } else {
        this.setMode('local');
        this.toast.show('Desktop alerts enabled', 'Alerts appear while this dashboard is open (server push not configured).', 'success');
      }
    } catch (err) {
      this.toast.show('Push subscription failed', errorMessage(err), 'error');
    }
  }

  private async disable(): Promise<void> {
    if (this.subscription) {
      const endpoint = this.subscription.endpoint;
      await this.subscription.unsubscribe().catch(() => undefined);
      await firstValueFrom(this.api.unsubscribePush(endpoint)).catch(() => undefined);
      this.subscription = null;
    }
    this.setMode('off');
    this.toast.show('Alerts disabled', '', 'info', 2500);
  }

  /** Local fallback: show a notification for High/Critical incidents while the tab is in the background. */
  notifyLocal(incident: IncidentAlert): void {
    if (this.mode() !== 'local' || !document.hidden || !this.registration) return;
    this.registration.showNotification(`${incident.severity.toUpperCase()} · Incident #${incident.id}`, {
      body: `${incident.zone} — humans in danger: ${incident.dangerCount}`,
      tag: `incident-${incident.id}`,
      requireInteraction: incident.severity === 'Critical',
      data: { url: location.href },
    });
  }

  private setMode(mode: 'push' | 'local' | 'off'): void {
    this.mode.set(mode);
    this.enabled.set(mode !== 'off');
  }
}
