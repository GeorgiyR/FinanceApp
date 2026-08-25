// @vitest-environment jsdom
import React from 'react';
import userEvent from '@testing-library/user-event';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

let responsiveContainerWidth = 960;
let responsiveContainerHeight = 220;

vi.mock('recharts', async () => {
  const actual = await vi.importActual<typeof import('recharts')>('recharts');
  return {
    ...actual,
    ResponsiveContainer: ({ children }: { children: React.ReactElement }) => (
      React.isValidElement(children)
        ? React.cloneElement(children, { width: responsiveContainerWidth, height: responsiveContainerHeight })
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

const fixtureFrankfurt1yVolumes = [
  60000, 58000, 55000, 52000, 50000, 47000, 45000, 43000, 41000, 39000, 37000, 35000, 33000,
  31000, 29000, 27000, 25000, 24000, 23000, 22000, 21000, 20000, 19000, 18000, 17000, 16000,
  15000, 14500, 14000, 13500, 13000, 12500, 12000, 18000, 17000, 16000, 15500, 15000, 14500,
  14000, 13500, 13000, 12500, 12000, 11500, 11000, 10500, 10000, 9500, 9000, 8500, 8000, 204,
];

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

const makeResponse = (range: 'today' | '1y' | '3y' | '5y', interval: string, volumes: number[]) => ({
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
    averageVolume20: 11363.8,
    averageVolume50: 20475.34,
    relativeVolume: null,
    turnover: null,
    turnoverCurrency: null,
    latestMetricsTimestamp: null,
    usesCompletedCandle: true,
  },
  points: volumes.map((volume, index) => makePoint(new Date(Date.UTC(2025, 0, 6 + index * 7)).toISOString(), interval, volume)),
});

const getRenderedBars = () => (
  Array.from(document.querySelectorAll<SVGElement>('.recharts-bar-rectangle .recharts-rectangle'))
    .map((element) => {
      const width = Number(element.getAttribute('width'));
      const height = Number(element.getAttribute('height'));
      const fill = (element.getAttribute('fill') ?? '').toLowerCase();
      const fillOpacityRaw = element.getAttribute('fill-opacity') ?? element.getAttribute('fillOpacity');
      const fillOpacity = fillOpacityRaw == null ? 1 : Number(fillOpacityRaw);
      return { element, width, height, fill, fillOpacity };
    })
    .filter((bar) => Number.isFinite(bar.width) && bar.width > 0 && Number.isFinite(bar.height) && bar.height > 0)
);

describe('StockPriceChart volume rendering regression', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    vi.clearAllMocks();
    responsiveContainerWidth = 960;
    responsiveContainerHeight = 220;
  });

  it('renders materially visible weekly volume bars for Frankfurt 1y robust mode', async () => {
    vi.mocked(api.getStockHistory).mockResolvedValueOnce({
      data: makeResponse('1y', '1wk', fixtureFrankfurt1yVolumes),
    } as never);

    render(<StockPriceChart panelId="rendering" stockId={11} ticker="ABEA" name="Alphabet Frankfurt" exchange="Frankfurt" providerSymbol="ABEA.F" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(11, '1y'));

    expect(screen.getByText('Объём по недельным свечам.')).toBeInTheDocument();
    expect(screen.getByText('Робастная шкала объёма: для долгого диапазона Frankfurt крупные выбросы визуально ограничены.')).toBeInTheDocument();

    const bars = getRenderedBars();
    const widths = bars.map((bar) => bar.width);
    const heights = bars.map((bar) => bar.height);

    expect(bars.length).toBeGreaterThan(20);
    expect(Math.min(...heights)).toBeGreaterThanOrEqual(3);
    expect(Math.min(...widths)).toBeGreaterThanOrEqual(4);

    const lastBar = bars[bars.length - 1];
    expect(lastBar?.height).toBeGreaterThanOrEqual(3);
    expect(lastBar?.width).toBeGreaterThanOrEqual(4);

    expect(bars.some((bar) => bar.fill === '#4096ff')).toBe(true);
    expect(bars.some((bar) => bar.fill === '#1677ff')).toBe(true);
    bars.forEach((bar) => {
      expect(bar.fillOpacity).toBeGreaterThanOrEqual(0.95);
    });
  });

  it('clamps long-range bar width in a narrow container without invalid dimensions', async () => {
    responsiveContainerWidth = 240;
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(() =>
      new DOMRect(0, 0, responsiveContainerWidth, responsiveContainerHeight));

    vi.mocked(api.getStockHistory).mockResolvedValueOnce({
      data: makeResponse('1y', '1wk', fixtureFrankfurt1yVolumes),
    } as never);

    render(<StockPriceChart panelId="rendering-narrow" stockId={12} ticker="ABEA" name="Alphabet Frankfurt" exchange="Frankfurt" providerSymbol="ABEA.F" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(12, '1y'));

    const bars = getRenderedBars();
    const widths = bars.map((bar) => bar.width);
    expect(widths.length).toBeGreaterThan(20);
    expect(widths.every((width) => Number.isFinite(width) && width > 0)).toBe(true);
    expect(Math.min(...widths)).toBeGreaterThan(1);
    expect(Math.max(...widths)).toBeLessThanOrEqual(14);
  });

  it('keeps visible clamped widths for 3y and 5y monthly ranges', async () => {
    const user = userEvent.setup();
    vi.mocked(api.getStockHistory).mockImplementation(async (_stockId: number, range: string) => {
      if (range === '3y') {
        return { data: makeResponse('3y', '1mo', Array.from({ length: 36 }, (_, index) => 12000 - index * 120)) } as never;
      }
      if (range === '5y') {
        return { data: makeResponse('5y', '1mo', Array.from({ length: 60 }, (_, index) => 16000 - index * 140)) } as never;
      }
      return { data: makeResponse('1y', '1wk', fixtureFrankfurt1yVolumes) } as never;
    });

    render(<StockPriceChart panelId="rendering-monthly" stockId={13} ticker="ABEA" name="Alphabet Frankfurt" exchange="Frankfurt" providerSymbol="ABEA.F" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(13, '1y'));

    await user.click(screen.getByText('3 года'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(13, '3y'));
    let widths = getRenderedBars().map((bar) => bar.width);
    expect(widths.length).toBeGreaterThan(20);
    expect(Math.min(...widths)).toBeGreaterThanOrEqual(4);
    expect(Math.max(...widths)).toBeLessThanOrEqual(14);

    await user.click(screen.getByText('5 лет'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(13, '5y'));
    widths = getRenderedBars().map((bar) => bar.width);
    expect(widths.length).toBeGreaterThan(20);
    expect(Math.min(...widths)).toBeGreaterThanOrEqual(4);
    expect(Math.max(...widths)).toBeLessThanOrEqual(14);
  });

  it('does not force long-range minimum width into intraday NYSE rendering', async () => {
    const user = userEvent.setup();
    vi.mocked(api.getStockHistory).mockImplementation(async (_stockId: number, range: string) => {
      if (range === 'today') {
        return { data: makeResponse('today', '5m', Array.from({ length: 320 }, (_, index) => 500 + (index % 20))) } as never;
      }
      return { data: makeResponse('1y', '1wk', fixtureFrankfurt1yVolumes) } as never;
    });

    render(<StockPriceChart panelId="rendering-nyse" stockId={14} ticker="AMD" name="AMD NYSE" exchange="NYSE" providerSymbol="AMD" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(14, '1y'));
    expect(screen.queryByText('Робастная шкала объёма: для долгого диапазона Frankfurt крупные выбросы визуально ограничены.')).not.toBeInTheDocument();

    await user.click(screen.getByText('Сегодня'));
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalledWith(14, 'today'));

    const widths = getRenderedBars().map((bar) => bar.width);
    expect(widths.length).toBeGreaterThan(50);
    expect(Math.min(...widths)).toBeGreaterThan(0);
    expect(Math.min(...widths)).toBeLessThan(4);
  });
});
