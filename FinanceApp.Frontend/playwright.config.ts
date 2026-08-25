import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/browser',
  timeout: 90_000,
  expect: {
    timeout: 10_000,
  },
  use: {
    baseURL: 'http://127.0.0.1:4173/financeapp/',
    viewport: { width: 1400, height: 1100 },
    headless: true,
  },
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1 --port 4173',
    url: 'http://127.0.0.1:4173/financeapp/stock-price-chart-volume-harness.html',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
