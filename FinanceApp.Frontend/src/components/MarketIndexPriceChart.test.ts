import { describe, expect, it } from 'vitest';
import { buildChartData } from './MarketIndexPriceChart';
import type { MarketIndexHistoryPoint } from '../types';

const point = (timestamp: string, close: number): MarketIndexHistoryPoint => ({
  timestamp,
  interval: '1wk',
  open: close,
  high: close,
  low: close,
  close,
  volume: 1000,
});

describe('MarketIndexPriceChart buildChartData', () => {
  it('appends a valid newer current snapshot for 1y', () => {
    const data = buildChartData(
      [point('2026-08-18T00:00:00.000Z', 100)],
      '1y',
      { price: 101, timestampUtc: '2026-08-19T12:00:00.000Z', isDelayed: false },
    );

    expect(data).toHaveLength(2);
    expect(data[1]?.timestamp).toBe('2026-08-19T12:00:00.000Z');
    expect(data[1]?.closeChart).toBe(101);
  });

  it('does not append equal/older/invalid/future/delayed snapshots for 1y', () => {
    const base = [point('2026-08-19T12:00:00.000Z', 100)];

    expect(buildChartData(base, '1y', { price: 101, timestampUtc: '2026-08-19T12:00:00.000Z', isDelayed: false })).toHaveLength(1);
    expect(buildChartData(base, '1y', { price: 101, timestampUtc: '2026-08-19T11:00:00.000Z', isDelayed: false })).toHaveLength(1);
    expect(buildChartData(base, '1y', { price: 101, timestampUtc: 'not-a-date', isDelayed: false })).toHaveLength(1);
    expect(buildChartData(base, '1y', { price: 101, timestampUtc: '2999-01-01T00:00:00.000Z', isDelayed: false })).toHaveLength(1);
    expect(buildChartData(base, '1y', { price: 101, timestampUtc: '2026-08-19T13:00:00.000Z', isDelayed: true })).toHaveLength(1);
  });
});
