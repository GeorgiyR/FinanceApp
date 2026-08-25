import { describe, expect, it } from 'vitest';
import {
  buildHistoryChartData,
  compressIntradaySessionGaps,
  formatHistoryTimestamp,
  TARGET_INTERSESSION_GAP_CSS_PX,
  usesUtcDateLabels,
} from './stockPriceChartData';
import type { StockHistoryPoint } from '../types';

const makeHistoryPoint = (timestamp: string, close: number, volume = 1000): StockHistoryPoint => ({
  timestamp,
  interval: '10m',
  openRaw: close,
  highRaw: close,
  lowRaw: close,
  closeRaw: close,
  openNormalized: close,
  highNormalized: close,
  lowNormalized: close,
  closeNormalized: close,
  openEur: close,
  highEur: close,
  lowEur: close,
  closeEur: close,
  volume,
});

describe('buildHistoryChartData', () => {
  it('keeps volume aligned with price points and inserts null gap markers for intraday gaps', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-08T10:00:00.000Z', 10, 1000),
      makeHistoryPoint('2026-08-08T14:30:00.000Z', 12, 2500),
    ], '24h');

    expect(data).toHaveLength(3);
    expect(data[0]).toMatchObject({ closeChart: 10, volumeChart: 1000 });
    expect(data[1]).toMatchObject({ closeChart: null, volumeChart: null, isGapMarker: true });
    expect(data[2]).toMatchObject({ closeChart: 12, volumeChart: 2500 });
  });

  it('preserves today range gap-marker behavior (no display-coordinate rewrite in data builder)', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-20T10:00:00.000Z', 10, 1000),
      makeHistoryPoint('2026-08-21T10:00:00.000Z', 11, 1200),
    ], 'today');

    expect(data).toHaveLength(3);
    expect(data[1]).toMatchObject({ closeChart: null, isGapMarker: true });
    expect(data.every((point) => point.displayX === undefined)).toBe(true);
  });

  it('keeps both sessions and real timestamps while compressing the overnight display gap to ~2cm', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-20T14:00:00.000Z', 99, 900),
      makeHistoryPoint('2026-08-20T16:00:00.000Z', 100, 1100),
      makeHistoryPoint('2026-08-21T08:05:00.000Z', 101, 1200),
    ], '24h');

    const compressed = compressIntradaySessionGaps(data, 1200);
    expect(data.map((point) => point.timestamp)).toEqual([
      '2026-08-20T14:00:00.000Z',
      '2026-08-20T16:00:00.000Z',
      '2026-08-20T16:00:00.001Z',
      '2026-08-21T08:05:00.000Z',
    ]);
    expect(compressed.map((point) => point.timestamp)).toEqual(data.map((point) => point.timestamp));

    const gapMarkerIndex = compressed.findIndex((point) => point.isGapMarker === true);
    expect(gapMarkerIndex).toBeGreaterThan(0);
    const marker = compressed[gapMarkerIndex];
    const firstCurrentSessionPoint = compressed[gapMarkerIndex + 1];
    expect(firstCurrentSessionPoint?.timestamp).toBe('2026-08-21T08:05:00.000Z');

    const compressedGapPx = (firstCurrentSessionPoint?.displayX ?? 0) - (marker?.displayX ?? 0);
    expect(compressedGapPx).toBeGreaterThan(70);
    expect(compressedGapPx).toBeLessThan(82);
  });

  it('keeps price and volume points aligned and recalculates the compressed gap on resize', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-20T14:00:00.000Z', 99, 900),
      makeHistoryPoint('2026-08-20T16:00:00.000Z', 100, 1100),
      makeHistoryPoint('2026-08-21T08:05:00.000Z', 101, 1200),
    ], '24h');

    const compressedWide = compressIntradaySessionGaps(data, 1200);
    const compressedNarrow = compressIntradaySessionGaps(data, 800);
    const markerIndex = compressedWide.findIndex((point) => point.isGapMarker === true);
    const wideGap = compressedWide[markerIndex + 1].displayX - compressedWide[markerIndex].displayX;
    const narrowGap = compressedNarrow[markerIndex + 1].displayX - compressedNarrow[markerIndex].displayX;

    expect(wideGap).toBeCloseTo(TARGET_INTERSESSION_GAP_CSS_PX, 1);
    expect(narrowGap).toBeCloseTo(TARGET_INTERSESSION_GAP_CSS_PX, 1);
    expect(compressedWide[2].displayX).not.toBeCloseTo(compressedNarrow[2].displayX, 3);
    expect(compressedWide.every((point) => Number.isFinite(point.displayX))).toBe(true);
    expect(compressedWide[0]).toMatchObject({ closeChart: 99, volumeChart: 900 });
    expect(compressedWide[1]).toMatchObject({ closeChart: 100, volumeChart: 1100 });
  });

  it('adds stable chart indexes for 1w data without breaking timestamp ordering', () => {
    const data = buildHistoryChartData([
      {
        timestamp: '2026-08-08T12:00:00.000Z',
        interval: '1h',
        openRaw: 20,
        highRaw: 20,
        lowRaw: 20,
        closeRaw: 20,
        openNormalized: 20,
        highNormalized: 20,
        lowNormalized: 20,
        closeNormalized: 20,
        openEur: null,
        highEur: null,
        lowEur: null,
        closeEur: null,
        volume: 400,
      },
      {
        timestamp: '2026-08-08T10:00:00.000Z',
        interval: '1h',
        openRaw: 18,
        highRaw: 18,
        lowRaw: 18,
        closeRaw: 18,
        openNormalized: 18,
        highNormalized: 18,
        lowNormalized: 18,
        closeNormalized: 18,
        openEur: null,
        highEur: null,
        lowEur: null,
        closeEur: null,
        volume: 300,
      },
    ], '1w');

    expect(data.map((point) => point.chartIndex)).toEqual([0, 1]);
    expect(data.map((point) => point.timestamp)).toEqual([
      '2026-08-08T10:00:00.000Z',
      '2026-08-08T12:00:00.000Z',
    ]);
    expect(data.map((point) => point.volumeChart)).toEqual([300, 400]);
  });

  it('appends a newer valid current quote for 1m/3m/6m ranges', () => {
    const data = buildHistoryChartData([
      {
        timestamp: '2026-08-18T00:00:00.000Z',
        interval: '1d',
        openRaw: 20,
        highRaw: 20,
        lowRaw: 20,
        closeRaw: 20,
        openNormalized: 20,
        highNormalized: 20,
        lowNormalized: 20,
        closeNormalized: 20,
        openEur: 20,
        highEur: 20,
        lowEur: 20,
        closeEur: 20,
        volume: 300,
      },
    ], '1m', {
      timestampUtc: '2026-08-19T13:45:00.000Z',
      closeChart: 21,
      rawClose: 21,
    });

    expect(data.map((point) => point.timestamp)).toEqual([
      '2026-08-18T00:00:00.000Z',
      '2026-08-19T13:45:00.000Z',
    ]);
    expect(data[1]).toMatchObject({ closeChart: 21, volumeChart: null });
  });

  it('does not append a duplicate daily trading date when the current quote is on the same effective date', () => {
    const data = buildHistoryChartData([
      {
        timestamp: '2026-08-19T00:00:00.000Z',
        interval: '1d',
        openRaw: 20,
        highRaw: 20,
        lowRaw: 20,
        closeRaw: 20,
        openNormalized: 20,
        highNormalized: 20,
        lowNormalized: 20,
        closeNormalized: 20,
        openEur: 20,
        highEur: 20,
        lowEur: 20,
        closeEur: 20,
        volume: 300,
      },
    ], '3m', {
      timestampUtc: '2026-08-19T22:30:00.000Z',
      closeChart: 22,
      rawClose: 22,
    });

    expect(data).toHaveLength(1);
    expect(data[0]?.closeChart).toBe(20);
  });

  it('does not append stale current quotes', () => {
    const data = buildHistoryChartData([
      {
        timestamp: '2026-08-18T00:00:00.000Z',
        interval: '1d',
        openRaw: 20,
        highRaw: 20,
        lowRaw: 20,
        closeRaw: 20,
        openNormalized: 20,
        highNormalized: 20,
        lowNormalized: 20,
        closeNormalized: 20,
        openEur: 20,
        highEur: 20,
        lowEur: 20,
        closeEur: 20,
        volume: 300,
      },
    ], '6m', {
      timestampUtc: '2026-08-19T13:45:00.000Z',
      closeChart: 21,
      rawClose: 21,
      isStale: true,
    });

    expect(data).toHaveLength(1);
    expect(data[0]?.closeChart).toBe(20);
  });

  it('does not append an older current quote overlay point', () => {
    const data = buildHistoryChartData([
      {
        timestamp: '2026-08-19T13:45:00.000Z',
        interval: '1d',
        openRaw: 20,
        highRaw: 20,
        lowRaw: 20,
        closeRaw: 20,
        openNormalized: 20,
        highNormalized: 20,
        lowNormalized: 20,
        closeNormalized: 20,
        openEur: 20,
        highEur: 20,
        lowEur: 20,
        closeEur: 20,
        volume: 300,
      },
    ], '1m', {
      timestampUtc: '2026-08-19T10:00:00.000Z',
      closeChart: 19,
      rawClose: 19,
      isStale: false,
    });

    expect(data).toHaveLength(1);
    expect(data[0]?.timestamp).toBe('2026-08-19T13:45:00.000Z');
  });

  it('keeps date-only labels on UTC trading dates for daily ranges at weekend boundaries', () => {
    expect(usesUtcDateLabels('1m')).toBe(true);
    expect(formatHistoryTimestamp('2026-08-21T22:30:00.000Z', '1m', 'DD.MM.YYYY')).toBe('21.08.2026');
  });

  it('appends a valid newer current snapshot for 1y weekly history', () => {
    const data = buildHistoryChartData([
      {
        timestamp: '2026-08-18T00:00:00.000Z',
        interval: '1wk',
        openRaw: 20,
        highRaw: 20,
        lowRaw: 20,
        closeRaw: 20,
        openNormalized: 20,
        highNormalized: 20,
        lowNormalized: 20,
        closeNormalized: 20,
        openEur: 20,
        highEur: 20,
        lowEur: 20,
        closeEur: 20,
        volume: 300,
      },
    ], '1y', {
      timestampUtc: '2026-08-19T13:45:00.000Z',
      closeChart: 21,
      rawClose: 21,
    });

    expect(data).toHaveLength(2);
    expect(data[1]?.timestamp).toBe('2026-08-19T13:45:00.000Z');
    expect(data[1]).toMatchObject({ closeChart: 21, volumeChart: null });
  });

  it('does not append equal/older/invalid/future snapshots for 1y', () => {
    const basePoint = {
      timestamp: '2026-08-19T13:45:00.000Z',
      interval: '1wk',
      openRaw: 20,
      highRaw: 20,
      lowRaw: 20,
      closeRaw: 20,
      openNormalized: 20,
      highNormalized: 20,
      lowNormalized: 20,
      closeNormalized: 20,
      openEur: 20,
      highEur: 20,
      lowEur: 20,
      closeEur: 20,
      volume: 300,
    } satisfies StockHistoryPoint;

    const equal = buildHistoryChartData([basePoint], '1y', {
      timestampUtc: '2026-08-19T13:45:00.000Z',
      closeChart: 21,
      rawClose: 21,
    });
    const older = buildHistoryChartData([basePoint], '1y', {
      timestampUtc: '2026-08-19T10:00:00.000Z',
      closeChart: 21,
      rawClose: 21,
    });
    const invalid = buildHistoryChartData([basePoint], '1y', {
      timestampUtc: 'not-a-date',
      closeChart: 21,
      rawClose: 21,
    });
    const future = buildHistoryChartData([basePoint], '1y', {
      timestampUtc: '2999-01-01T00:00:00.000Z',
      closeChart: 21,
      rawClose: 21,
    });

    expect(equal).toHaveLength(1);
    expect(older).toHaveLength(1);
    expect(invalid).toHaveLength(1);
    expect(future).toHaveLength(1);
  });
});
