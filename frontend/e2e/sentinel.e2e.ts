import { expect, test } from '@playwright/test';
import { mockApi, problem, signIn } from './mock-api';

// MOCK-BASED browser tests: every API and SignalR response below is hand-written. No real model output, no backend, no database.

const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
const model = (name: string, loaded: boolean) => ({ name, file: `${name}.onnx`, license: 'test', categories: ['Fire'], threshold: 0.4, loaded, error: loaded ? null : 'model file not found' });

test('login: validation, wrong password, then sign-in reaches the dashboard', async ({ page }) => {
  let attempt = 0;
  const log = await mockApi(page, {
    'POST /api/auth/login': (route) => (++attempt === 1
      ? problem(401, 'Unauthorized', 'Invalid email or password.')(route)
      : route.fulfill({ json: { token: 'e2e-token', expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
          user: { id: 1, email: 'admin@test.local', displayName: 'Admin', role: 'Admin' } } })),
  });
  await page.goto('/login');

  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByText('Enter a valid email address.')).toBeVisible();
  await expect(page.getByText('Password must be at least 6 characters.')).toBeVisible();
  expect(log.handled).toEqual([]); // invalid form: no request sent

  await page.getByLabel('Email').fill('admin@test.local');
  await page.getByLabel('Password').fill('wrong-password');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('alert')).toHaveText('Invalid email or password.');
  await expect(page).toHaveURL(/\/login$/);

  await page.getByLabel('Password').fill('right-password');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole('heading', { name: 'Alert Logs' })).toBeVisible();
  expect(log.handled.filter((k) => k === 'POST /api/auth/login')).toHaveLength(2);
});

test('login: an unreachable API is reported, not shown as a wrong password', async ({ page }) => {
  await mockApi(page, { 'POST /api/auth/login': (route) => route.abort('connectionrefused') });
  await page.goto('/login');
  await page.getByLabel('Email').fill('admin@test.local');
  await page.getByLabel('Password').fill('any-password');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('alert')).toHaveText('Cannot reach the API server. Is the backend running?');
});

test('AI status: online with all models, then offline', async ({ page }) => {
  await signIn(page);
  let online = true;
  await mockApi(page, {
    'GET /api/ai/status': (route) => route.fulfill({ json: online
      ? { configured: true, online: true, error: null, service: { status: 'ok', version: '1.0.0', capabilities: ['Fire', 'Human', 'Smoke'],
          models: [model('person', true), model('fire_smoke', true)], inference: { count: 0, avgMs: null, lastMs: null }, cameras: [] } }
      : { configured: true, online: false, error: 'AI service unreachable at http://127.0.0.1:8008.', service: null } }),
  });
  await page.goto('/ai');
  await expect(page.getByText('ONLINE · all models loaded')).toBeVisible();

  online = false;
  await page.reload();
  await expect(page.getByRole('alert').filter({ hasText: 'OFFLINE' })).toHaveText(/OFFLINE · AI service unreachable at http:\/\/127\.0\.0\.1:8008\./);
  await expect(page.getByText('ONLINE')).toHaveCount(0);
});

test('AI upload: a wrong file type is refused in the browser, a failed analysis shows the server error', async ({ page }) => {
  await signIn(page);
  const log = await mockApi(page, {
    'GET /api/ai/status': (route) => route.fulfill({ json: { configured: true, online: false, error: 'offline', service: null } }),
    'POST /api/ai/analyze': problem(503, 'AI service unavailable', 'AI service unreachable at http://127.0.0.1:8008. Start ai-service (see ai-service/README.md).'),
  });
  await page.goto('/ai');
  const input = page.locator('input[type=file]');

  await input.setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('not an image') });
  await expect(page.getByText('Choose a PNG or JPG image.')).toBeVisible();
  expect(log.handled).not.toContain('POST /api/ai/analyze');

  await input.setInputFiles({ name: 'fire.png', mimeType: 'image/png', buffer: PNG });
  await expect(page.getByRole('alert').filter({ hasText: 'Start ai-service' }))
    .toHaveText('AI service unreachable at http://127.0.0.1:8008. Start ai-service (see ai-service/README.md).');
  expect(log.handled).toContain('POST /api/ai/analyze');
});

test('alerts: incidents from the realtime snapshot are listed with severity, place, source and status', async ({ page }) => {
  await signIn(page);
  const now = new Date().toISOString();
  const incident = (id: number, extra: object) => ({ id, timestamp: now, updatedAt: now, cameraId: null, cameraName: null, sensorId: null,
    fireDetected: false, fireSeverity: null, humanCount: 0, dangerCount: 0, message: '', isResolved: false, resolvedAt: null, ...extra });
  await mockApi(page, {}, {
    cameras: [], sensors: [], sprinklers: [], serverTime: now,
    system: { level: 'critical', openIncidents: 1, activeSprinklers: 0 },
    incidents: [
      incident(7, { source: 'Vision', zone: 'Ground Floor - Lobby', severity: 'Critical', cameraId: 'cam-lobby', cameraName: 'Main Lobby',
        fireDetected: true, fireSeverity: 'Critical', humanCount: 2, dangerCount: 1, message: 'Fire detected (Critical) on Main Lobby' }),
      incident(3, { source: 'Sensor', zone: 'First Floor - Server Hall', severity: 'Medium', sensorId: 'smoke-c1', isResolved: true, resolvedAt: now }),
    ],
  });
  await page.goto('/');
  await expect(page.getByText('Live', { exact: true })).toBeVisible(); // mocked SignalR link is up

  const alerts = page.getByRole('region', { name: 'Alert Logs' });
  const open = alerts.getByRole('row').filter({ hasText: 'Main Lobby' });
  await expect(open).toContainText('Critical');
  await expect(open).toContainText('Ground Floor - Lobby (Main Lobby)');
  await expect(open).toContainText('AI Vision');
  await expect(open).toContainText('Open');
  await expect(alerts.getByRole('row').filter({ hasText: 'smoke-c1' })).toHaveCount(0); // resolved: hidden under "Open"

  await alerts.getByRole('tab', { name: 'All' }).click();
  const resolved = alerts.getByRole('row').filter({ hasText: 'smoke-c1' });
  await expect(resolved).toContainText('First Floor - Server Hall (sensor smoke-c1)');
  await expect(resolved).toContainText('Resolved');
});

test('status banner: no incidents with the AI offline is "degraded", never "all systems nominal"', async ({ page }) => {
  await signIn(page);
  let online = false;
  const now = new Date().toISOString();
  await mockApi(page, {
    'GET /api/ai/status': (route) => route.fulfill({ json: online
      ? { configured: true, online: true, error: null, service: { status: 'ok', version: '1.0.0', capabilities: ['Fire', 'Human', 'Smoke'],
          models: [model('person', true), model('fire_smoke', true)], inference: { count: 0, avgMs: null, lastMs: null }, cameras: [] } }
      : { configured: true, online: false, error: 'AI service unreachable', service: null } }),
  }, { cameras: [], sensors: [], sprinklers: [], incidents: [], serverTime: now, system: { level: 'nominal', openIncidents: 0, activeSprinklers: 0 } });
  await page.goto('/');
  const banner = page.getByRole('status').filter({ hasText: /NOMINAL|DEGRADED/ });
  await expect(banner).toHaveText('MONITORING DEGRADED · AI OFFLINE');

  online = true;
  await page.reload();
  await expect(banner).toHaveText('ALL SYSTEMS NOMINAL');
});

test('status banner: a camera the AI cannot read makes monitoring "degraded" even without a camera-lost incident', async ({ page }) => {
  await signIn(page);
  const now = new Date().toISOString();
  const camera = (state: string) => ({ cameraId: 'cam-kitchen', kind: 'stream', source: 'rtsp://***@10.0.0.20/live', simulated: false, state,
    error: state === 'running' ? null : 'cannot open stream source', framesSent: 0, postFailures: 0 });
  let state = 'error';
  await mockApi(page, {
    'GET /api/ai/status': (route) => route.fulfill({ json: { configured: true, online: true, error: null, service: { status: 'ok', version: '1.0.0',
      capabilities: ['Fire', 'Human', 'Smoke'], models: [model('person', true), model('fire_smoke', true)], inference: { count: 0, avgMs: null, lastMs: null },
      cameras: [camera(state)] } } }),
  }, { cameras: [], sensors: [], sprinklers: [], incidents: [], serverTime: now, system: { level: 'nominal', openIncidents: 0, activeSprinklers: 0 } });
  await page.goto('/');
  const banner = page.getByRole('status').filter({ hasText: /NOMINAL|DEGRADED/ });
  await expect(banner).toHaveText('MONITORING DEGRADED · CAMERA OFFLINE');

  state = 'running';
  await page.reload();
  await expect(banner).toHaveText('ALL SYSTEMS NOMINAL');
});

test('status banner follows AI and camera health within one 10 s poll', async ({ page }) => {
  await page.clock.install();
  await signIn(page);
  const now = new Date().toISOString();
  type Health = { reply: 'ok' | 'http503' | 'modelMissing'; camera: string };
  const h: Health = { reply: 'ok', camera: 'running' };
  const camera = { cameraId: 'cam-kitchen', kind: 'stream', source: 'rtsp://***@10.0.0.20/live', simulated: false, error: null, framesSent: 1, postFailures: 0 };
  await mockApi(page, {
    'GET /api/ai/status': (route) => h.reply === 'http503'
      ? route.fulfill({ status: 503, json: { title: 'unavailable' } })
      : route.fulfill({ json: { configured: true, online: true, error: null, service: { status: h.reply === 'ok' ? 'ok' : 'degraded', version: '1.0.0',
          capabilities: ['Fire'], models: [model('person', true), model('fire_smoke', h.reply === 'ok')], inference: { count: 0, avgMs: null, lastMs: null },
          cameras: [{ ...camera, state: h.camera }] } } }),
  }, { cameras: [], sensors: [], sprinklers: [], incidents: [], serverTime: now, system: { level: 'nominal', openIncidents: 0, activeSprinklers: 0 } });
  await page.goto('/');
  const banner = page.getByRole('status').filter({ hasText: /NOMINAL|DEGRADED/ });
  await expect(banner).toHaveText('ALL SYSTEMS NOMINAL');

  const nextPoll = async (state: Partial<Health>, text: string) => {
    Object.assign(h, state);
    await page.clock.runFor(10_500); // one 10 s poll plus scheduling tolerance
    await expect(banner).toHaveText(text);
  };
  await nextPoll({ camera: 'error' }, 'MONITORING DEGRADED · CAMERA OFFLINE');
  await nextPoll({ camera: 'reconnecting' }, 'MONITORING DEGRADED · CAMERA OFFLINE');
  await nextPoll({ camera: 'running' }, 'ALL SYSTEMS NOMINAL');                   // healthy again after reconnect
  await nextPoll({ camera: 'stopped' }, 'ALL SYSTEMS NOMINAL');                   // deleted on purpose: not a failure
  await nextPoll({ reply: 'modelMissing', camera: 'running' }, 'MONITORING DEGRADED · AI MODEL MISSING');
  await nextPoll({ reply: 'http503' }, 'MONITORING DEGRADED · AI STATUS UNKNOWN'); // health could not be read
});

const aiOk = { configured: true, online: true, error: null, service: { status: 'ok', version: '1.0.0', capabilities: ['Fire', 'Human', 'Smoke'],
  models: [model('person', true), model('fire_smoke', true)], inference: { count: 0, avgMs: null, lastMs: null }, cameras: [] } };
const aiDown = { configured: true, online: false, error: 'AI service unreachable', service: null };
const kitchenCamera = { id: 'cam-k', name: 'Kitchen', feedType: 'CCTV', streamUrl: '', streamFormat: 'Simulated', status: 'Online',
  locationZone: 'First Floor - Kitchen', updatedAt: new Date().toISOString() };
const frame = (cameraId: string) => ({ cameraId, timestamp: new Date().toISOString(), frameWidth: 640, frameHeight: 480, normalized: false,
  detections: [], dangerZones: [], heatmap: null, humanCoordinates: [], fireDetected: false, fireSeverity: null, humanCount: 0, dangerCount: 0,
  simulated: false, smokeDetected: false, model: 'person fire_smoke' });

test('IL-9: AI dies between polls -> "AI DATA STALE" within 9 s, then AI OFFLINE; an open incident keeps the reason visible', async ({ page }) => {
  await page.clock.install();
  await signIn(page);
  let health: object = aiOk;
  const now = new Date().toISOString();
  const log = await mockApi(page, { 'GET /api/ai/status': (route) => route.fulfill({ json: health }) },
    { cameras: [kitchenCamera], sensors: [], sprinklers: [], incidents: [], serverTime: now, system: { level: 'nominal', openIncidents: 0, activeSprinklers: 0 } });
  await page.goto('/');
  const banner = page.getByRole('status').filter({ hasText: /NOMINAL|DEGRADED|OPEN/ });
  await expect(page.getByText('Live', { exact: true })).toBeVisible(); // the (mock) realtime link is up: frames sent now are received
  for (let i = 0; i < 5; i++) { log.push('detection', frame('cam-k')); await page.clock.runFor(1_000); } // ~1 frame/s, like the live AI
  await expect(banner).toHaveText('ALL SYSTEMS NOMINAL');

  await page.clock.runFor(9_000);                       // frames stopped; the last poll still said "ok"
  await expect(banner).toHaveText('MONITORING DEGRADED · AI DATA STALE');

  health = aiDown;
  await page.clock.runFor(10_500);                      // next poll
  await expect(banner).toHaveText('MONITORING DEGRADED · AI OFFLINE');

  log.push('incident', { action: 'created', incident: { id: 9, timestamp: now, updatedAt: now, cameraId: 'cam-k', cameraName: 'Kitchen', sensorId: null,
    source: 'CameraLost', zone: 'First Floor - Kitchen', severity: 'Medium', fireDetected: false, fireSeverity: null, humanCount: 0, dangerCount: 0,
    message: 'Camera lost: no frames from Kitchen for 10 s', isResolved: false, resolvedAt: null } });
  await expect(banner).toHaveText(/^(ELEVATED RISK|CRITICAL ALERT) · 1 OPEN · AI OFFLINE\s*$/);   // real count + the reason, not hidden

  health = aiOk;                                         // AI back: frames flow again (~1/s) through the next poll
  for (let i = 0; i < 11; i++) { log.push('detection', frame('cam-k')); await page.clock.runFor(1_000); }
  await expect(banner).toHaveText(/^(ELEVATED RISK|CRITICAL ALERT) · 1 OPEN\s*$/);              // healthy again; the incident stays open
  await expect(page.getByRole('region', { name: 'Alert Logs' }).getByRole('row').filter({ hasText: 'Kitchen' })).toContainText('Open');
});

