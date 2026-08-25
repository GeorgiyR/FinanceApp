// @vitest-environment jsdom
import React from 'react';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('recharts', async () => {
  const actual = await vi.importActual<typeof import('recharts')>('recharts');
  return {
    ...actual,
    ResponsiveContainer: ({ children }: { children: React.ReactElement }) => (
      React.isValidElement(children)
        ? React.cloneElement(children, { width: 960, height: 220 })
        : null
    ),
  };
});

import StockPriceChart from './StockPriceChart';
import * as api from '../services/api';

vi.mock('../services/api', () => ({
  getStockHistory: vi.fn(),
  refreshStockHistory: vi.fn(),
  getStockHistoryRoutingDiagnostics: vi.fn(),
  hardResetStockHistory: vi.fn(),
  validateStockHistoryProviderSymbol: vi.fn(),
  getIndexConstituentHistory: vi.fn(),
}));

vi.mock('./StockTechnicalAnalysisPanel', () => ({
  default: () => <div data-testid="technical-analysis-panel" />,
}));

const fixtureVolumes = [
  60000, 58000, 55000, 52000, 50000, 47000, 45000, 43000, 41000, 39000, 37000, 35000, 33000,
  31000, 29000, 27000, 25000, 24000, 23000, 22000, 21000, 20000, 19000, 18000, 17000, 16000,
  15000, 14500, 14000, 13500, 13000, 12500, 12000, 18000, 17000, 16000, 15500, 15000, 14500,
  14000, 13500, 13000, 12500, 12000, 11500, 11000, 10500, 10000, 9500, 9000, 8500, 8000, 932,
];

describe('StockPriceChart volume rendering regression', () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders materially visible weekly volume bars for Frankfurt 1y robust mode', async () => {
    vi.mocked(api.getStockHistory).mockResolvedValueOnce({
      data: {
        range: '1y',
        interval: '1wk',
        currency: 'EUR',
        financialCurrency: 'EUR',
        normalizedQuoteCurrency: 'EUR',
        quoteUnitMultiplier: 1,
        rateToEur: null,
        rateTimestampUtc: null,
        rateSource: null,
        conversionWarning: null,
        volumeMetrics: {
          averageVolume20: 11363.8,
          averageVolume50: 20475.34,
          relativeVolume: null,
          turnover: null,
          turnoverCurrency: null,
          latestMetricsTimestamp: null,
          usesCompletedCandle: true,
        },
        points: fixtureVolumes.map((volume, index) => ({
          timestamp: new Date(Date.UTC(2025, 0, 6 + index * 7)).toISOString(),
          interval: '1wk',
          openRaw: 100,
          highRaw: 110,
          lowRaw: 90,
          closeRaw: 100,
          openNormalized: 100,
          highNormalized: 110,
          lowNormalized: 90,
          closeNormalized: 100,
          openEur: 100,
          highEur: 110,
          lowEur: 90,
          closeEur: 100,
          volume,
          isQuoteDerived: false,
        })),
      },
    } as never);

    render(<StockPriceChart panelId="rendering" stockId={11} ticker="ABEA" name="Alphabet Frankfurt" exchange="Frankfurt" providerSymbol="ABEA.F" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(11, '1y'));

    expect(screen.getByText('Объём по недельным свечам.')).toBeInTheDocument();
    expect(screen.getByText('Робастная шкала объёма: для долгого диапазона Frankfurt крупные выбросы визуально ограничены.')).toBeInTheDocument();

    const barRectangles = Array.from(document.querySelectorAll<SVGElement>('.recharts-bar-rectangle .recharts-rectangle, .recharts-bar-rectangle'));
    const heights = barRectangles
      .map((element) => Number(element.getAttribute('height')))
      .filter((value) => Number.isFinite(value) && value > 0);

    expect(heights.length).toBeGreaterThan(20);
    expect(Math.max(...heights)).toBeGreaterThan(20);
    expect(Math.min(...heights)).toBeGreaterThanOrEqual(3);

    const lastBar = barRectangles[barRectangles.length - 1];
    expect(Number(lastBar?.getAttribute('height'))).toBeGreaterThanOrEqual(3);

  });
});
