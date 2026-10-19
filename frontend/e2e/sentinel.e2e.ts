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
