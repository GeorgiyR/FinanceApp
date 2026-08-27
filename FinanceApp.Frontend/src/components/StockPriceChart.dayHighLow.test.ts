import { describe, expect, it } from 'vitest';
import { readFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';
import type { StockHistoryPoint, StockHistoryRange, StockQuoteResponse } from '../types';
import { STOCKS_TABLE_TOTAL_COLS } from '../pages/StocksPage';
import {
  PORTFOLIO_EXECUTED_ORDER_RIGHT_ALIGNED_MONEY_KEYS,
  PORTFOLIO_PENDING_ORDER_RIGHT_ALIGNED_MONEY_KEYS,
  PORTFOLIO_POSITION_RIGHT_ALIGNED_MONEY_KEYS,
  PORTFOLIO_TRANSACTION_RIGHT_ALIGNED_MONEY_KEYS,
} from '../pages/PortfolioDetailPage';
import {
  CURRENT_PRICE_FONT_SIZE,
  DAY_HIGH_LOW_VALUE_FONT_SIZE,
  RANGE_MIN_MAX_LABELS,
  getDayHighLowDisplay,
  getDayRangeLabel,
} from './dayHighLow';
import {
  DAY_RANGE_ARROW_TEXT,
  RANGE_BOUND_COLOR,
  BASELINE_BLOCK_STYLE,
  PERIOD_CHANGE_HEADING,
} from './StockPriceChart';

const __dirname = dirname(fileURLToPath(import.meta.url));
const stockPriceChartSource = readFileSync(join(__dirname, 'StockPriceChart.tsx'), 'utf-8');

const makePoint = (overrides: Partial<StockHistoryPoint> = {}): StockHistoryPoint => ({
  timestamp: '2026-08-08T00:00:00Z',
  interval: '1d',
  openRaw: 100,
  highRaw: 110,
  lowRaw: 90,
  closeRaw: 105,
  openNormalized: 100,
  highNormalized: 110,
  lowNormalized: 90,
  closeNormalized: 105,
  openEur: 100,
  highEur: 110,
  lowEur: 90,
  closeEur: 105,
  volume: 1000,
  ...overrides,
});

const makeQuote = (overrides: Partial<StockQuoteResponse> = {}): StockQuoteResponse => ({
  symbol: 'RHM.DE',
  rawCurrentPrice: 100,
  rawPreviousClose: 99,
  rawChange: 1,
  currency: 'USD',
  financialCurrency: 'USD',
  normalizedQuoteCurrency: 'USD',
  quoteUnitMultiplier: 1,
  normalizedCurrentPrice: 100,
  normalizedPreviousClose: 99,
  normalizedChange: 1,
  currentPriceEur: null,
  changeEur: null,
  percentChange: 1,
  rawDayHigh: null,
  rawDayLow: null,
  normalizedDayHigh: null,
  normalizedDayLow: null,
  dayHighEur: null,
  dayLowEur: null,
  marketState: 'REGULAR',
  priceSession: 'REGULAR',
  priceTimestampUtc: null,
  isStale: false,
  delayWarning: null,
  priceSource: null,
  rateToEur: null,
  rateTimestampUtc: null,
  rateSource: null,
  conversionWarning: null,
  ...overrides,
});

describe('range min/max aggregation', () => {
  it('uses lows/highs (not closes) for min/max', () => {
    const points = [
      makePoint({ timestamp: '2026-08-07T00:00:00Z', lowEur: 99, highEur: 101, closeEur: 50 }),
      makePoint({ timestamp: '2026-08-08T00:00:00Z', lowEur: 95, highEur: 120, closeEur: 500 }),
    ];
    const display = getDayHighLowDisplay(null, points, true);
    expect(display.minimum.value).toBe(95);
    expect(display.maximum.value).toBe(120);
  });

  it('recomputes from selected-range data', () => {
    const range1y = getDayHighLowDisplay(null, [makePoint({ lowEur: 90, highEur: 120 })], true);
    const range1m = getDayHighLowDisplay(null, [makePoint({ lowEur: 102, highEur: 108 })], true);
    expect(range1y.minimum.value).toBe(90);
    expect(range1m.minimum.value).toBe(102);
  });

  it('prefers EUR values, else normalized, else raw', () => {
    const eur = getDayHighLowDisplay(null, [makePoint({ lowEur: 10, highEur: 20 })], true);
    expect(eur.minimum.value).toBe(10);
    expect(eur.maximum.value).toBe(20);

    const normalized = getDayHighLowDisplay(
      null,
      [makePoint({ lowEur: null, highEur: null, lowNormalized: 11, highNormalized: 22 })],
      false,
    );
    expect(normalized.minimum.value).toBe(11);
    expect(normalized.maximum.value).toBe(22);

    const raw = getDayHighLowDisplay(
      null,
      [
        makePoint({
          lowEur: null,
          highEur: null,
          lowNormalized: null as unknown as number,
          highNormalized: null as unknown as number,
          lowRaw: 12,
          highRaw: 23,
        }),
      ],
      false,
    );
    expect(raw.minimum.value).toBe(12);
    expect(raw.maximum.value).toBe(23);
  });

  it('ignores missing low/high independently', () => {
    const display = getDayHighLowDisplay(
      null,
      [
        makePoint({ lowEur: null, highEur: 200 }),
        makePoint({ lowEur: 80, highEur: null }),
      ],
      true,
    );
    expect(display.minimum.value).toBe(80);
    expect(display.maximum.value).toBe(200);
  });

  it('returns occurrence timestamps for min and max', () => {
    const display = getDayHighLowDisplay(
      null,
      [
        makePoint({ timestamp: '2026-08-06T00:00:00Z', lowEur: 95, highEur: 101 }),
        makePoint({ timestamp: '2026-08-07T00:00:00Z', lowEur: 90, highEur: 99 }),
        makePoint({ timestamp: '2026-08-08T00:00:00Z', lowEur: 98, highEur: 120 }),
      ],
      true,
    );
    expect(display.minimum.timestampUtc).toBe('2026-08-07T00:00:00Z');
    expect(display.maximum.timestampUtc).toBe('2026-08-08T00:00:00Z');
  });

  it('includes incomplete current-day candle as part of selected period', () => {
    const display = getDayHighLowDisplay(
      null,
      [
        makePoint({ timestamp: '2026-08-08T00:00:00Z', lowEur: 90, highEur: 120 }),
        makePoint({ timestamp: '2026-08-09T12:00:00Z', interval: '5m', lowEur: 70, highEur: 110 }),
      ],
      true,
    );
    expect(display.minimum.value).toBe(70);
  });

  it('includes fresher same-day live bounds only when that day is inside history points', () => {
    const quote = makeQuote({
      priceTimestampUtc: '2026-08-09T12:10:00Z',
      dayLowEur: 65,
      dayHighEur: 130,
      rawDayLow: 6500,
      rawDayHigh: 13000,
    });

    const withToday = getDayHighLowDisplay(
      quote,
      [makePoint({ timestamp: '2026-08-09T12:00:00Z', interval: '5m', lowEur: 70, highEur: 120 })],
      true,
    );
    expect(withToday.minimum.value).toBe(65);
    expect(withToday.maximum.value).toBe(130);
    expect(withToday.minimum.isFromLiveQuote).toBe(true);

    const withoutToday = getDayHighLowDisplay(
      quote,
      [makePoint({ timestamp: '2026-08-08T00:00:00Z', lowEur: 90, highEur: 120 })],
      true,
    );
    expect(withoutToday.minimum.value).toBe(90);
    expect(withoutToday.maximum.value).toBe(120);
  });
});

describe('dynamic Russian labels by selected range', () => {
  const cases: Array<[StockHistoryRange, string]> = [
    ['today', 'Мин.–макс. сегодня'],
    ['24h', 'Мин.–макс. за 24 ч.'],
    ['1w', 'Мин.–макс. за 1 нед.'],
    ['1m', 'Мин.–макс. за 1 мес.'],
    ['3m', 'Мин.–макс. за 3 мес.'],
    ['6m', 'Мин.–макс. за 6 мес.'],
    ['1y', 'Мин.–макс. за 1 год'],
    ['3y', 'Мин.–макс. за 3 года'],
    ['5y', 'Мин.–макс. за 5 лет'],
  ];

  it.each(cases)('%s label is exact', (range, label) => {
    expect(RANGE_MIN_MAX_LABELS[range]).toBe(label);
    expect(getDayRangeLabel(range)).toBe(label);
  });
});

describe('compact block presentation contract', () => {
  it('keeps 16px range text and current price', () => {
    expect(DAY_HIGH_LOW_VALUE_FONT_SIZE).toBe(16);
    expect(CURRENT_PRICE_FONT_SIZE).toBe(16);
    expect(DAY_HIGH_LOW_VALUE_FONT_SIZE).toBeGreaterThanOrEqual(CURRENT_PRICE_FONT_SIZE);
  });

  it('keeps exact spaced arrow and secondary gray color in the range line', () => {
    expect(DAY_RANGE_ARROW_TEXT).toBe(' → ');
    expect(RANGE_BOUND_COLOR).toBe('#8c8c8c');
  });

  it('baseline block is pushed right with marginLeft auto and right-aligned text', () => {
    expect(BASELINE_BLOCK_STYLE.marginLeft).toBe('auto');
    expect(BASELINE_BLOCK_STYLE.textAlign).toBe('right');
  });

  it('uses the exact renamed period-change heading', () => {
    expect(PERIOD_CHANGE_HEADING).toBe('Изменение от начала периода');
  });
});

describe('selected-period summary layout contract', () => {
  it('renders baseline value before the right-aligned change block', () => {
    const baselineValueIdx = stockPriceChartSource.indexOf('formatCurrencyValue(periodSummary.baselineValue, displayCurrencyCode)');
    const rightBlockIdx = stockPriceChartSource.indexOf('<div style={BASELINE_BLOCK_STYLE}>');
    expect(baselineValueIdx).toBeGreaterThan(-1);
    expect(rightBlockIdx).toBeGreaterThan(-1);
    expect(baselineValueIdx).toBeLessThan(rightBlockIdx);
  });

  it('keeps change value inside the right-aligned block under the renamed heading', () => {
    expect(stockPriceChartSource).toContain('const periodChangeHeading = periodSummary.changeHeading ?? PERIOD_CHANGE_HEADING;');
    expect(stockPriceChartSource).toContain('{periodChangeHeading}');
    expect(stockPriceChartSource).toContain("<div style={{ color: performanceColor ?? 'inherit', fontWeight: 600 }}>");
    expect(stockPriceChartSource).toContain('formatCurrencyValue(periodChangeValue, displayCurrencyCode)');
  });

  it('keeps green/red color logic for positive and negative change', () => {
    expect(stockPriceChartSource).toContain('periodChangeValue >= 0');
    expect(stockPriceChartSource).toContain('? COLOR_POSITIVE');
    expect(stockPriceChartSource).toContain(': COLOR_NEGATIVE');
  });
});

describe('table structures remain unchanged', () => {
  it('stocks table still has 8 columns', () => {
    expect(STOCKS_TABLE_TOTAL_COLS).toBe(8);
  });

  it('portfolio table alignment keys still do not include range min/max fields', () => {
    const allKeys = [
      ...PORTFOLIO_POSITION_RIGHT_ALIGNED_MONEY_KEYS,
      ...PORTFOLIO_PENDING_ORDER_RIGHT_ALIGNED_MONEY_KEYS,
      ...PORTFOLIO_EXECUTED_ORDER_RIGHT_ALIGNED_MONEY_KEYS,
      ...PORTFOLIO_TRANSACTION_RIGHT_ALIGNED_MONEY_KEYS,
    ];
    expect(allKeys).not.toContain('minimum');
    expect(allKeys).not.toContain('maximum');
  });
});
