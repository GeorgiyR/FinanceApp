import { describe, expect, it } from 'vitest';
import { readFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const appSource = readFileSync(join(__dirname, 'App.tsx'), 'utf-8');

describe('App market-indices route', () => {
  it('registers /market-indices inside PrivateRoute', () => {
    expect(appSource).toMatch(/path="\/market-indices"[\s\S]*?<PrivateRoute>[\s\S]*?<MarketIndicesPage \/>/);
  });

  it('registers /stocks/catalog inside PrivateRoute', () => {
    expect(appSource).toMatch(/path="\/stocks\/catalog"[\s\S]*?<PrivateRoute>[\s\S]*?<StocksPage mode="catalog" \/>/);
  });

  it('registers /help and /help/:articleSlug inside PrivateRoute', () => {
    expect(appSource).toMatch(/path="\/help"[\s\S]*?<PrivateRoute>[\s\S]*?<HelpPage \/>/);
    expect(appSource).toMatch(/path="\/help\/:articleSlug"[\s\S]*?<PrivateRoute>[\s\S]*?<HelpPage \/>/);
  });

  it('registers /system/processes inside PrivateRoute', () => {
    expect(appSource).toMatch(/path="\/system\/processes"[\s\S]*?<PrivateRoute>[\s\S]*?<SystemProcessRunsPage \/>/);
  });
});
