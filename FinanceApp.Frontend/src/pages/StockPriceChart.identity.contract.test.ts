import { readFileSync } from 'fs';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { describe, expect, it } from 'vitest';
import { analyzeAdaptiveVolumeScale } from '../components/stockVolumeScale';

const __dirname = dirname(fileURLToPath(import.meta.url));
const portfolioDetailSource = readFileSync(join(__dirname, 'PortfolioDetailPage.tsx'), 'utf8');
const stocksPageSource = readFileSync(join(__dirname, 'StocksPage.tsx'), 'utf8');
const indexConstituentsSource = readFileSync(join(__dirname, '../components/IndexConstituentsPanel.tsx'), 'utf8');

describe('StockPriceChart listing identity wiring', () => {
  it('passes exchange/providerSymbol from portfolio chart row into StockPriceChart props', () => {
    expect(portfolioDetailSource).toContain('exchange={item?.stock?.exchange ?? null}');
    expect(portfolioDetailSource).toContain('providerSymbol={item?.stock?.providerSymbol ?? null}');
  });

  it('keeps stocks and index constituent chart call sites passing listing identity', () => {
    expect(stocksPageSource).toContain('exchange={stock?.exchange ?? null}');
    expect(stocksPageSource).toContain('providerSymbol={stock?.providerSymbol ?? null}');
    expect(stocksPageSource).toContain('exchange={stock.exchange}');
    expect(stocksPageSource).toContain('providerSymbol={stock.providerSymbol ?? null}');
    expect(indexConstituentsSource).toContain('exchange={stock?.exchange ?? null}');
    expect(indexConstituentsSource).toContain('providerSymbol={stock?.providerSymbol ?? null}');
  });

  it('activates adaptive Frankfurt mode once Frankfurt identity reaches chart analysis', () => {
    const analysis = analyzeAdaptiveVolumeScale('Frankfurt', [120, 160, 200, 300, 42000]);
    expect(analysis.adaptiveScaleActive).toBe(true);
  });
});
