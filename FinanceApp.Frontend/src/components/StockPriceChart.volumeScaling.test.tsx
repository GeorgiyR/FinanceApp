// @vitest-environment jsdom
import React from 'react';
import userEvent from '@testing-library/user-event';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
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

const makePoint = (timestamp: string, interval: string, volume: number) => ({
  timestamp,
  interval,
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
});

const makeResponse = (range: '1y' | '3y' | '5y', interval: string, volumes: number[]) => ({
  range,
  interval,
  currency: 'EUR',
  financialCurrency: 'EUR',
  normalizedQuoteCurrency: 'EUR',
  quoteUnitMultiplier: 1,
  rateToEur: null,
  rateTimestampUtc: null,
  rateSource: null,
  conversionWarning: null,
  volumeMetrics: {
    averageVolume20: null,
    averageVolume50: null,
    relativeVolume: null,
    turnover: null,
    turnoverCurrency: null,
    latestMetricsTimestamp: null,
    usesCompletedCandle: true,
  },
  points: volumes.map((volume, index) => makePoint(`2026-08-${String(index + 1).padStart(2, '0')}T00:00:00Z`, interval, volume)),
});

describe('StockPriceChart volume scaling behavior', () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders Frankfurt 1y volume section without no-volume fallback', async () => {
    vi.mocked(api.getStockHistory).mockResolvedValueOnce({
      data: makeResponse('1y', '1wk', [120, 160, 200, 300, 42000]),
    } as never);

    render(<StockPriceChart panelId="p1" stockId={1} ticker="AMD" name="AMD Frankfurt" exchange="Frankfurt" providerSymbol="AMD.F" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalled());

    expect(screen.getByText('Объём и активность торгов')).toBeInTheDocument();
    expect(screen.queryByText('Поставщик не предоставил данные об объёме')).not.toBeInTheDocument();
  });

  it('renders explicit no-volume message when provider returned no positive finite volumes', async () => {
    vi.mocked(api.getStockHistory).mockResolvedValueOnce({
      data: makeResponse('1y', '1wk', [0, 0, 0, 0, 0]),
    } as never);

    render(<StockPriceChart panelId="p2" stockId={2} ticker="SAP" name="SAP Frankfurt" exchange="Frankfurt" providerSymbol="SAP.F" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalled());

    expect(screen.getByText('Поставщик не предоставил данные об объёме')).toBeInTheDocument();
  });

  it('keeps positive-volume rendering for 3y and 5y ranges', async () => {
    const user = userEvent.setup();
    vi.mocked(api.getStockHistory).mockImplementation(async (_stockId: number, range: string) => {
      if (range === '3y') {
        return { data: makeResponse('3y', '1mo', [10, 20, 30, 40, 50]) } as never;
      }
      if (range === '5y') {
        return { data: makeResponse('5y', '1mo', [10, 20, 30, 40, 50]) } as never;
      }
      return { data: makeResponse('1y', '1wk', [10, 20, 30, 40, 50]) } as never;
    });

    render(<StockPriceChart panelId="p3" stockId={3} ticker="BAS" name="BASF" exchange="Frankfurt" providerSymbol="BAS.F" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(3, '1y'));
    expect(screen.queryByText('Поставщик не предоставил данные об объёме')).not.toBeInTheDocument();

    await user.click(screen.getByText('3 года'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(3, '3y'));
    expect(screen.queryByText('Поставщик не предоставил данные об объёме')).not.toBeInTheDocument();

    await user.click(screen.getByText('5 лет'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(3, '5y'));
    expect(screen.queryByText('Поставщик не предоставил данные об объёме')).not.toBeInTheDocument();
  });
});
