// @vitest-environment jsdom
import React from 'react';
import userEvent from '@testing-library/user-event';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

let responsiveContainerWidth = 960;
let responsiveContainerHeight = 240;
const renderedReferenceDots: Array<{ x: unknown; y: unknown; ifOverflow?: unknown; label?: unknown }> = [];

vi.mock('recharts', async () => {
  const actual = await vi.importActual<typeof import('recharts')>('recharts');
  return {
    ...actual,
    ResponsiveContainer: ({ children }: { children: React.ReactElement }) => (
      React.isValidElement(children)
        ? React.cloneElement(children, { width: responsiveContainerWidth, height: responsiveContainerHeight })
        : null
    ),
    ReferenceDot: (props: { x: unknown; y: unknown; ifOverflow?: unknown; label?: unknown }) => {
      renderedReferenceDots.push(props);
      return <g data-testid="reference-dot" />;
    },
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

const makeResponse = (overrides: Partial<ReturnType<typeof buildBaseResponse>> = {}) => ({
  ...buildBaseResponse(),
  ...overrides,
});

const buildBaseResponse = () => ({
  range: 'today' as const,
  interval: '10m',
  currency: 'USD',
  financialCurrency: 'USD',
  normalizedQuoteCurrency: 'USD',
  quoteUnitMultiplier: 1,
  rateToEur: 1,
  rateTimestampUtc: '2026-08-27T15:40:00Z',
  rateSource: 'test',
  conversionWarning: null,
  asOfUtc: '2026-08-27T15:40:00Z',
  currentSessionHasCandles: false,
  unavailableReason: null,
  volumeMetrics: {
    averageVolume20: null,
    averageVolume50: null,
    relativeVolume: null,
    turnover: null,
    turnoverCurrency: null,
    latestMetricsTimestamp: null,
    usesCompletedCandle: true,
  },
  points: [
    {
      timestamp: '2026-08-26T14:30:00Z',
      interval: '10m',
      openRaw: 267,
      highRaw: 268,
      lowRaw: 266,
      closeRaw: 267,
      openNormalized: 267,
      highNormalized: 268,
      lowNormalized: 266,
      closeNormalized: 267,
      openEur: 267,
      highEur: 268,
      lowEur: 266,
      closeEur: 267,
      volume: 2000,
      isQuoteDerived: false,
    },
    {
      timestamp: '2026-08-26T16:40:00Z',
      interval: '10m',
      openRaw: 273.5,
      highRaw: 274.1,
      lowRaw: 272.8,
      closeRaw: 274,
      openNormalized: 273.5,
      highNormalized: 274.1,
      lowNormalized: 272.8,
      closeNormalized: 274,
      openEur: 273.5,
      highEur: 274.1,
      lowEur: 272.8,
      closeEur: 274,
      volume: 2100,
      isQuoteDerived: false,
    },
  ],
});

const makeLiveQuote = (overrides: Partial<Parameters<typeof renderChart>[0]['liveQuote']> = {}) => ({
  symbol: 'TEST.F',
  rawCurrentPrice: 273.4,
  rawPreviousClose: 282,
  rawChange: -8.6,
  normalizedCurrentPrice: 273.4,
  normalizedPreviousClose: 282,
  normalizedChange: -8.6,
  currentPriceEur: 273.4,
  changeEur: -8.6,
  changePercent: -3.05,
  percentChange: -3.05,
  rawDayHigh: null,
  rawDayLow: null,
  normalizedDayHigh: null,
  normalizedDayLow: null,
  dayHighEur: null,
  dayLowEur: null,
  marketState: 'CLOSED',
  priceSession: 'POST',
  priceTimestampUtc: '2026-08-27T16:40:00Z',
  isStale: false,
  currency: 'USD',
  financialCurrency: 'USD',
  normalizedQuoteCurrency: 'USD',
  quoteUnitMultiplier: 1,
  delayWarning: null,
  priceSource: null,
  rateToEur: 1,
  rateTimestampUtc: '2026-08-27T16:40:00Z',
  rateSource: 'test',
  conversionWarning: null,
  ...overrides,
});

const renderChart = (params: {
  response?: ReturnType<typeof makeResponse>;
  liveQuote?: ReturnType<typeof makeLiveQuote> | null;
}) => render(
  <StockPriceChart
    panelId="session-baseline"
    stockId={41}
    ticker="TEST"
    name="Test Instrument"
    exchange="Frankfurt"
    providerSymbol="TEST.F"
    liveQuote={params.liveQuote ?? makeLiveQuote()}
  />,
);

describe('StockPriceChart session baseline presentation', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    vi.clearAllMocks();
    responsiveContainerWidth = 960;
    responsiveContainerHeight = 240;
    renderedReferenceDots.length = 0;
  });

  it('shows previous-close/current-quote markers, previous-close heading, and negative red change for today', async () => {
    const user = userEvent.setup();
    vi.mocked(api.getStockHistory).mockImplementation(async (_stockId: number, range: string) => {
      if (range === 'today') {
        return { data: makeResponse() } as never;
      }
      return { data: { ...makeResponse(), range: '1y' as const } } as never;
    });

    renderChart({});
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(41, '1y'));
    await user.click(screen.getByText('Сегодня'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(41, 'today'));

    expect(screen.getByText('Изменение к предыдущему закрытию')).toBeInTheDocument();
    expect(screen.getAllByText('Пред. закрытие: €282.00').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Текущая цена: €273.40').length).toBeGreaterThan(0);
    expect(screen.getByText(/предыдущее закрытие из котировки/i)).toBeInTheDocument();
    expect(screen.getByText(/€-8\.60 \(-3\.05%\)/)).toHaveStyle({ color: '#cf1322' });
    expect(screen.queryByTestId('reference-dot')).not.toBeInTheDocument();
    expect(renderedReferenceDots.some((dot) => dot.ifOverflow === 'extendDomain')).toBe(false);
  });

  it('renders positive move vs previous close in green with plus sign', async () => {
    const user = userEvent.setup();
    vi.mocked(api.getStockHistory).mockImplementation(async (_stockId: number, range: string) => {
      if (range === 'today') {
        return { data: makeResponse() } as never;
      }
      return { data: { ...makeResponse(), range: '1y' as const } } as never;
    });

    renderChart({
      liveQuote: makeLiveQuote({
        currentPriceEur: 273.4,
        changeEur: 5.4,
        normalizedCurrentPrice: 273.4,
        normalizedChange: 5.4,
        normalizedPreviousClose: 268,
      }),
    });
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(41, '1y'));
    await user.click(screen.getByText('Сегодня'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(41, 'today'));

    expect(screen.getByText(/€5\.40 \(\+2\.01%\)/)).toHaveStyle({ color: '#389e0d' });
  });

  it('does not show mismatch warning when previous close matches last historical close within tolerance', async () => {
    const user = userEvent.setup();
    vi.mocked(api.getStockHistory).mockImplementation(async (_stockId: number, range: string) => {
      if (range === 'today') {
        return { data: makeResponse() } as never;
      }
      return { data: { ...makeResponse(), range: '1y' as const } } as never;
    });

    renderChart({
      liveQuote: makeLiveQuote({
        currentPriceEur: 274.01,
        changeEur: 0.01,
        normalizedCurrentPrice: 274.01,
        normalizedChange: 0.01,
        normalizedPreviousClose: 274,
      }),
    });
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(41, '1y'));
    await user.click(screen.getByText('Сегодня'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(41, 'today'));

    expect(screen.queryByText(/предыдущее закрытие из котировки/i)).not.toBeInTheDocument();
    expect(screen.getAllByTestId('reference-dot')).toHaveLength(1);
    expect(renderedReferenceDots[0]?.ifOverflow).toBeUndefined();
  });

  it('keeps selected-period heading when previous-close baseline is unavailable', async () => {
    const user = userEvent.setup();
    vi.mocked(api.getStockHistory).mockImplementation(async (_stockId: number, range: string) => {
      if (range === 'today') {
        return { data: makeResponse({ rateToEur: null }) } as never;
      }
      return { data: { ...makeResponse(), range: '1y' as const } } as never;
    });

    renderChart({
      liveQuote: makeLiveQuote({
        currentPriceEur: null,
        changeEur: null,
      }),
    });
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(41, '1y'));
    await user.click(screen.getByText('Сегодня'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(41, 'today'));

    expect(screen.getByText('Изменение от начала периода')).toBeInTheDocument();
  });
});
