import { describe, expect, it } from 'vitest';
import type { StockQuoteResponse } from '../types';
import {
  resolveNewestCurrentPriceSnapshot,
  shouldKeepLiveOverlayAfterPersistedRefresh,
} from './currentPriceSnapshot';

const makeQuote = (overrides: Partial<StockQuoteResponse> = {}): StockQuoteResponse => ({
  symbol: 'TST',
  rawCurrentPrice: 1500,
  rawPreviousClose: 1400,
  rawChange: 100,
  currency: 'USD',
  financialCurrency: 'USD',
  normalizedQuoteCurrency: 'USD',
  quoteUnitMultiplier: 1,
  normalizedCurrentPrice: 1500,
  normalizedPreviousClose: 1400,
  normalizedChange: 100,
  currentPriceEur: 1350,
  changeEur: -50,
  percentChange: -3.57,
  rawDayHigh: null,
  rawDayLow: null,
  normalizedDayHigh: null,
  normalizedDayLow: null,
  dayHighEur: null,
  dayLowEur: null,
  marketState: 'REGULAR',
  priceSession: 'REGULAR',
  priceTimestampUtc: '2026-08-19T10:00:00Z',
  isStale: false,
  delayWarning: null,
  priceSource: null,
  rateToEur: 0.9,
  rateTimestampUtc: '2026-08-19T10:00:00Z',
  rateSource: 'ecb',
  conversionWarning: null,
  ...overrides,
});

describe('shouldKeepLiveOverlayAfterPersistedRefresh', () => {
  const now = Date.parse('2026-08-19T12:00:00Z');

  it('drops equal-timestamp live overlays after authoritative persisted refresh', () => {
    const keep = shouldKeepLiveOverlayAfterPersistedRefresh(
      {
        currentPrice: 150,
        currentPriceAt: '2026-08-19T10:00:00Z',
        currentPriceIsDelayed: false,
        currentPriceDelayWarning: null,
      },
      makeQuote({
        currentPriceEur: 150,
        priceTimestampUtc: '2026-08-19T10:00:00Z',
        isStale: false,
      }),
      now,
    );

    expect(keep).toBe(false);
  });

  it('drops older live overlays and keeps genuinely newer ones', () => {
    const older = shouldKeepLiveOverlayAfterPersistedRefresh(
      {
        currentPrice: 150,
        currentPriceAt: '2026-08-19T10:30:00Z',
        currentPriceIsDelayed: false,
        currentPriceDelayWarning: null,
      },
      makeQuote({
        currentPriceEur: 149,
        priceTimestampUtc: '2026-08-19T10:00:00Z',
      }),
      now,
    );
    const newer = shouldKeepLiveOverlayAfterPersistedRefresh(
      {
        currentPrice: 150,
        currentPriceAt: '2026-08-19T10:00:00Z',
        currentPriceIsDelayed: false,
        currentPriceDelayWarning: null,
      },
      makeQuote({
        currentPriceEur: 151,
        priceTimestampUtc: '2026-08-19T10:45:00Z',
      }),
      now,
    );

    expect(older).toBe(false);
    expect(newer).toBe(true);
  });
});

describe('resolveNewestCurrentPriceSnapshot', () => {
  it('uses newer delayed quote instead of older persisted snapshot', () => {
    const selected = resolveNewestCurrentPriceSnapshot(
      {
        currentPrice: 1400,
        currentPriceChange: 25,
        currentPriceChangePercent: 1.82,
        currentPriceAt: '2026-08-19T09:00:00Z',
        currentPriceIsDelayed: false,
        currentPriceDelayWarning: null,
      },
      makeQuote({
        currentPriceEur: 1350,
        changeEur: -50,
        percentChange: -3.57,
        priceTimestampUtc: '2026-08-19T10:00:00Z',
        isStale: true,
        delayWarning: 'Котировка задержана на 15 мин',
      }),
      Date.parse('2026-08-19T12:00:00Z'),
    );

    expect(selected.source).toBe('live');
    expect(selected.currentPrice).toBe(1350);
    expect(selected.currentPriceChange).toBe(-50);
    expect(selected.currentPriceChangePercent).toBe(-3.57);
    expect(selected.currentPriceAt).toBe('2026-08-19T10:00:00Z');
    expect(selected.isDelayed).toBe(true);
    expect(selected.delayWarning).toContain('задержана');
  });

  it('keeps newer persisted snapshot when delayed quote is older', () => {
    const selected = resolveNewestCurrentPriceSnapshot(
      {
        currentPrice: 1400,
        currentPriceChange: 25,
        currentPriceChangePercent: 1.82,
        currentPriceAt: '2026-08-19T10:30:00Z',
        currentPriceIsDelayed: false,
        currentPriceDelayWarning: null,
      },
      makeQuote({
        currentPriceEur: 1350,
        changeEur: -50,
        percentChange: -3.57,
        priceTimestampUtc: '2026-08-19T10:00:00Z',
        isStale: true,
        delayWarning: 'Котировка задержана',
      }),
      Date.parse('2026-08-19T12:00:00Z'),
    );

    expect(selected.source).toBe('persisted');
    expect(selected.currentPrice).toBe(1400);
    expect(selected.currentPriceChange).toBe(25);
    expect(selected.currentPriceChangePercent).toBe(1.82);
    expect(selected.currentPriceAt).toBe('2026-08-19T10:30:00Z');
    expect(selected.isDelayed).toBe(false);
  });

  it('prefers a non-delayed snapshot for equal timestamps', () => {
    const selected = resolveNewestCurrentPriceSnapshot(
      {
        currentPrice: 1400,
        currentPriceChange: 25,
        currentPriceChangePercent: 1.82,
        currentPriceAt: '2026-08-19T10:00:00Z',
        currentPriceIsDelayed: true,
        currentPriceDelayWarning: 'Котировка задержана',
      },
      makeQuote({
        currentPriceEur: 1350,
        changeEur: -50,
        percentChange: -3.57,
        priceTimestampUtc: '2026-08-19T10:00:00Z',
        isStale: false,
        delayWarning: null,
      }),
      Date.parse('2026-08-19T12:00:00Z'),
    );

    expect(selected.source).toBe('live');
    expect(selected.currentPrice).toBe(1350);
    expect(selected.isDelayed).toBe(false);
  });

  it('falls back deterministically on missing/invalid timestamps', () => {
    const withBrokenTimestamps = resolveNewestCurrentPriceSnapshot(
      {
        currentPrice: 1400,
        currentPriceChange: 25,
        currentPriceChangePercent: 1.82,
        currentPriceAt: 'not-a-date',
        currentPriceIsDelayed: true,
        currentPriceDelayWarning: 'Котировка задержана',
      },
      makeQuote({
        currentPriceEur: 1350,
        priceTimestampUtc: null,
      }),
      Date.parse('2026-08-19T12:00:00Z'),
    );
    expect(withBrokenTimestamps.source).toBe('persisted');
    expect(withBrokenTimestamps.currentPrice).toBe(1400);
    expect(withBrokenTimestamps.isDelayed).toBe(true);

    const liveOnly = resolveNewestCurrentPriceSnapshot(
      {
        currentPrice: null,
        currentPriceChange: null,
        currentPriceChangePercent: null,
        currentPriceAt: 'bad',
        currentPriceIsDelayed: false,
        currentPriceDelayWarning: null,
      },
      makeQuote({ currentPriceEur: 1350, priceTimestampUtc: null }),
      Date.parse('2026-08-19T12:00:00Z'),
    );
    expect(liveOnly.source).toBe('live');
    expect(liveOnly.currentPrice).toBe(1350);
  });

  it('normalizes timezone-less timestamps as UTC and keeps EUR-normalized values coherent', () => {
    const selected = resolveNewestCurrentPriceSnapshot(
      {
        currentPrice: 1400,
        currentPriceChange: 25,
        currentPriceChangePercent: 1.82,
        currentPriceAt: '2026-08-19T10:00:00Z',
        currentPriceIsDelayed: false,
        currentPriceDelayWarning: null,
      },
      makeQuote({
        rawCurrentPrice: 1500,
        currency: 'USD',
        currentPriceEur: 1350,
        changeEur: -50,
        percentChange: -3.57,
        priceTimestampUtc: '2026-08-19T10:30:00',
      }),
      Date.parse('2026-08-19T12:00:00Z'),
    );

    expect(selected.source).toBe('live');
    expect(selected.currentPrice).toBe(1350);
    expect(selected.currentPriceChange).toBe(-50);
    expect(selected.currentPriceChangePercent).toBe(-3.57);
    expect(selected.currentPriceAt).toBe('2026-08-19T10:30:00');
  });
});
