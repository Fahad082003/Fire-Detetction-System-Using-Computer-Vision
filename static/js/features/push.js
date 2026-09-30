import { toast } from '../core/dom.js';

function urlBase64ToUint8Array(base64) {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4);
  const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

/**
 * Web Push subscription manager. With VAPID configured, alerts arrive through the
 * service worker even when the tab is closed. Without it, falls back to local
 * notifications raised from WebSocket events while the tab is in the background.
 */
export class PushController {
  constructor(api, button) {
    this.api = api;
    this.button = button;
    this.registration = null;
    this.vapidKey = null;
    this.subscription = null;
    this.localEnabled = false;
    this.supported = 'serviceWorker' in navigator && 'Notification' in window;
    button.addEventListener('click', () => this.toggle());
  }

  async init() {
    if (!this.supported) {
      this.button.hidden = true;
      return;
    }
    try {
      this.registration = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
      const { vapid_public_key: key } = await this.api.pushConfig();
      this.vapidKey = key;
      if (key && 'PushManager' in window) {
        this.subscription = await this.registration.pushManager.getSubscription();
      }
      this.localEnabled = !key && Notification.permission === 'granted';
    } catch (err) {
      console.warn('[push] init failed', err);
    }
    this._render();
  }

  get enabled() {
    return Boolean(this.subscription) || this.localEnabled;
  }

  async toggle() {
    if (this.enabled) return this.disable();
    return this.enable();
  }

  async enable() {
    const permission = await Notification.requestPermission();
    if (permission !== 'granted') {
      toast('Notifications blocked', 'Allow notifications for this site in your browser settings.', 'warning');
      return;
    }
    try {
      if (this.vapidKey && this.registration && 'PushManager' in window) {
        this.subscription = await this.registration.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: urlBase64ToUint8Array(this.vapidKey),
        });
        await this.api.subscribePush(this.subscription.toJSON());
        toast('Push alerts enabled', 'This device will receive incident alerts even when closed.', 'success');
      } else {
        this.localEnabled = true;
        toast('Desktop alerts enabled', 'Alerts will appear while this dashboard is open.', 'success');
      }
    } catch (err) {
      toast('Push subscription failed', err.message, 'error');
    }
    this._render();
  }

  async disable() {
    try {
      if (this.subscription) {
        const { endpoint } = this.subscription;
        await this.subscription.unsubscribe();
        await this.api.unsubscribePush(endpoint).catch(() => {});
        this.subscription = null;
      }
      this.localEnabled = false;
      toast('Alerts disabled', '', 'info', 2500);
    } catch (err) {
      toast('Could not disable alerts', err.message, 'error');
    }
    this._render();
  }

  /** Local fallback for incident alerts when server push is not configured. */
  notifyLocal(incident) {
    if (!this.localEnabled || this.subscription || !document.hidden || !this.registration) return;
    this.registration.showNotification(`${incident.severity.toUpperCase()} · Incident #${incident.id}`, {
      body: `${incident.location} — humans: ${incident.human_count}, threats: ${incident.danger_count}`,
      tag: `incident-${incident.id}`,
      renotify: true,
      requireInteraction: incident.severity === 'Critical',
      data: { url: location.href },
    });
  }

  _render() {
    this.button.dataset.state = this.enabled ? 'on' : 'off';
    this.button.textContent = this.enabled ? 'Alerts On' : 'Enable Alerts';
    this.button.title = this.subscription ? 'Web Push active' : this.localEnabled ? 'Local desktop notifications' : 'Enable alert notifications';
  }
}
