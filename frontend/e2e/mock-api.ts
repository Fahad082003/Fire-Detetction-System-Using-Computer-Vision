import { Page, Route } from '@playwright/test';

/** Answers every request to the real API address inside the browser; nothing is sent to the network. */
export type Handler = (route: Route) => Promise<void> | void;
export interface ApiLog { handled: string[]; unmocked: string[]; push: (target: string, arg: object) => void }

const API = /^https?:\/\/(localhost|127\.0\.0\.1|\[::1\]):5291\//;
const API_SOCKET = /^wss?:\/\/(localhost|127\.0\.0\.1|\[::1\]):5291\//; // SignalR connects with ws://, not http://

/** handlers: "METHOD /path" -> response. Anything else gets a 503 "not mocked" and is listed in log.unmocked. */
export async function mockApi(page: Page, handlers: Record<string, Handler> = {}, snapshot?: object): Promise<ApiLog> {
  let socket: { send: (m: string) => void } | null = null;
  const log: ApiLog = { handled: [], unmocked: [],
    push: (target, arg) => socket?.send(JSON.stringify({ type: 1, target, arguments: [arg] }) + '\u001e') }; // server -> dashboard event
  await page.route(API, async (route) => {
    const req = route.request();
    const key = `${req.method()} ${new URL(req.url()).pathname}`;
    const handler = handlers[key] ?? (key === 'POST /hubs/monitoring/negotiate' && snapshot ? negotiate : undefined);
    if (handler) { log.handled.push(key); return handler(route); }
    log.unmocked.push(key);
    return route.fulfill({ status: 503, json: { title: 'Not mocked in e2e', detail: key } });
  });
  // SignalR WebSocket, fully mocked: never connected to a server.
  await page.routeWebSocket(API_SOCKET, (ws) => {
    socket = ws;
    ws.onMessage((message) => {
      if (String(message).includes('"type":6')) { ws.send('{"type":6}\u001e'); return; } // answer keep-alive pings, or fast-forwarded clocks time out
      if (!snapshot || !String(message).includes('"protocol"')) return; // invocations are ignored
      ws.send('{}\u001e'); // handshake accepted
      ws.send(JSON.stringify({ type: 1, target: 'snapshot', arguments: [snapshot] }) + '\u001e');
    });
  });
  return log;
}

const negotiate: Handler = (route) => route.fulfill({
  json: { negotiateVersion: 1, connectionId: 'e2e', connectionToken: 'e2e', availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }] },
});

/** Logged-in session in localStorage, as AuthService stores it, so pages behind the guard open without a login request. */
export async function signIn(page: Page, role: 'Admin' | 'Operator' = 'Admin'): Promise<void> {
  const session = { token: 'e2e-token', expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
    user: { id: 1, email: 'e2e@test.local', displayName: 'E2E User', role } };
  await page.addInitScript((s) => localStorage.setItem('sentinel.auth', s), JSON.stringify(session));
}

export const problem = (status: number, title: string, detail: string): Handler =>
  (route) => route.fulfill({ status, contentType: 'application/problem+json', body: JSON.stringify({ title, status, detail }) });
