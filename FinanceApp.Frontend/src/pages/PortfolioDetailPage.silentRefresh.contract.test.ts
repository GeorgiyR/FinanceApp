import { describe, expect, it } from 'vitest';
import { readFileSync } from 'fs';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const readPageSource = () =>
  readFileSync(join(__dirname, 'PortfolioDetailPage.tsx'), 'utf8');

describe('PortfolioDetailPage silent refresh contracts', () => {
  it('wires silent authoritative refetch on focus and visible document state', () => {
    const source = readPageSource();
    expect(source).toContain("window.addEventListener('focus', handleFocus)");
    expect(source).toContain("document.addEventListener('visibilitychange', handleVisibilityChange)");
    expect(source).toContain("document.visibilityState === 'visible'");
    expect(source).toContain('void silentRefetchPortfolioData()');
  });

  it('uses deduplicated in-flight promise and cleanup for listeners/timer', () => {
    const source = readPageSource();
    expect(source).toContain('silentRefetchInFlightRef.current');
    expect(source).toContain('window.setInterval');
    expect(source).toContain('PORTFOLIO_SILENT_REFRESH_INTERVAL_MS');
    expect(source).toContain('window.clearInterval(intervalId)');
    expect(source).toContain("window.removeEventListener('focus', handleFocus)");
    expect(source).toContain("document.removeEventListener('visibilitychange', handleVisibilityChange)");
  });

  it('keeps silent refetch out of full-page loading path', () => {
    const source = readPageSource();
    expect(source).toContain('if (!silent) {');
    expect(source).toContain('setLoading(true);');
    expect(source).toContain('setLoading(false);');
    expect(source).toContain('await fetchData(true);');
  });
});
