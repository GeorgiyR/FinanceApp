import { describe, expect, it } from 'vitest';
import { readFileSync } from 'fs';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(__dirname, 'StockPriceChart.tsx'), 'utf8');

describe('StockPriceChart history strategy contracts', () => {
  it('renders the price-history chart block above the technical analysis panel', () => {
    const historyTitleIdx = source.indexOf('История цены: {ticker} — {name}');
    const technicalPanelIdx = source.indexOf('<StockTechnicalAnalysisPanel stockId={stockId} />');
    expect(historyTitleIdx).toBeGreaterThan(-1);
    expect(technicalPanelIdx).toBeGreaterThan(-1);
    expect(historyTitleIdx).toBeLessThan(technicalPanelIdx);
  });

  it('keeps generic tracked-stock history loader/refresh behavior as default', () => {
    expect(source).toContain('getStockHistory(stockId, historyRange)');
    expect(source).toContain('const refreshRes = await refreshStockHistory(stockId);');
  });

  it('supports index constituent context via explicit adapter props', () => {
    expect(source).toContain('historyLoader?:');
    expect(source).toContain('historyRefreshJobAdapter?:');
    expect(source).toContain('runIndexConstituentHistoryRefreshJob');
    expect(source).toContain('getIndexConstituentHistory(indexId, stockId, historyRange)');
    expect(source).toContain('if (notice.refreshChart) {');
    expect(source).toContain('await fetchHistory();');
  });

  it('prevents duplicate starts while refresh is in progress', () => {
    expect(source).toContain('if (historyRefreshing) {');
    expect(source).toContain('setHistoryRefreshing(true);');
    expect(source).toContain('setHistoryRefreshing(false);');
  });

  it('selects a coherent newest snapshot for current price and chart overlay', () => {
    expect(source).toContain('resolveNewestCurrentPriceSnapshot');
    expect(source).toContain('const selectedSessionSnapshot = useMemo(');
    expect(source).toContain('timestampUtc: selectedSessionSnapshot.currentPriceAt');
    expect(source).toContain('isStale: selectedSessionSnapshot.isDelayed');
  });

  it('keeps adaptive-volume semantics display-only and preserves actual tooltip values', () => {
    expect(source).toContain('dataKey="volumeDisplay"');
    expect(source).toContain('formatVolumeTooltipValue(payload.volumeChart, payload.volumeCapped === true)');
    expect(source).toContain('Поставщик не предоставил данные об объёме');
    expect(source).toContain('minPointSize={volumeScale.adaptiveScaleActive ? 3 : 1}');
  });
});
