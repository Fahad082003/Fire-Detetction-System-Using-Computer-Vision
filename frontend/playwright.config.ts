import { defineConfig } from '@playwright/test';

/**
 * Mock-only browser tests (no backend, no database). Run: npx playwright test
 * - Every request to the real API address (localhost:5291) is answered inside the browser by e2e/mock-api.ts.
 * - e2e/tripwire.ts listens on :5291 during the run and fails it if anything connects.
 * - Uses the installed Microsoft Edge; Playwright downloads no browser.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  globalSetup: './e2e/tripwire.ts',
  workers: 1,
  reporter: 'list',
  use: {
    channel: 'msedge',
    headless: true,
    baseURL: 'http://localhost:4300',
    serviceWorkers: 'block', // a service worker could send requests that page.route() does not see
  },
  webServer: {
    command: 'npx ng serve --port 4300', // not 4200: that may be your own dev server
    url: 'http://localhost:4300',
    reuseExistingServer: false,
    timeout: 180_000,
  },
});
