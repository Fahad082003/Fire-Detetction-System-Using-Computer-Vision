/* Sentinel service worker — Web Push delivery for incident alerts. */
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));

self.addEventListener('push', (event) => {
  let data = {};
  try {
    data = event.data ? event.data.json() : {};
  } catch {
    data = { body: event.data ? event.data.text() : '' };
  }
  const title = data.title || 'Sentinel alert';
  const critical = data.severity === 'Critical' || data.severity === 'High';
  event.waitUntil(self.registration.showNotification(title, {
    body: data.body || 'A new incident requires attention.',
    tag: data.tag || 'sentinel-alert',
    renotify: true,
    requireInteraction: Boolean(data.requireInteraction),
    vibrate: critical ? [300, 120, 300, 120, 300] : [200],
    data: { url: data.url || '/', incidentId: data.incident_id },
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const target = new URL(event.notification.data?.url || '/', self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const existing = windows.find((w) => w.url.startsWith(self.location.origin));
    if (existing) return existing.focus();
    return self.clients.openWindow(target);
  })());
});
