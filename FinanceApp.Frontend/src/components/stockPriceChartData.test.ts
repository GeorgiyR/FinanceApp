import { describe, expect, it } from 'vitest';
import {
  buildHistoryChartData,
  comparePreviousCloseToHistoryEndpoint,
  compressIntradaySessionGaps,
  formatHistoryTimestamp,
  PREVIOUS_SESSION_DISPLAY_RATIO,
  SESSION_GAP_DISPLAY_RATIO,
  PRIMARY_SESSION_DISPLAY_RATIO,
  PREVIOUS_CLOSE_MISMATCH_ABSOLUTE_TOLERANCE,
  PREVIOUS_CLOSE_MISMATCH_RELATIVE_TOLERANCE,
  PREVIOUS_SESSION_TAIL_MAX_POINTS,
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
  it('excludes quote-derived points from intraday sessions and plotted history', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-08T10:00:00.000Z', 10, 1000),
      { ...makeHistoryPoint('2026-08-08T16:40:00.000Z', 11, 1100), isQuoteDerived: true },
      makeHistoryPoint('2026-08-09T10:00:00.000Z', 12, 2500),
    ], '24h', null, { currentSessionHasCandles: false });

    expect(data).toHaveLength(1);
    expect(data.some((point) => point.isQuoteDerived === true)).toBe(false);
    expect(data[0]).toMatchObject({ closeChart: 12, sessionRole: 'primary' });
  });

  it('keeps volume aligned with price points and inserts null gap markers for intraday gaps', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-08T10:00:00.000Z', 10, 1000),
      makeHistoryPoint('2026-08-08T11:00:00.000Z', 11, 1200),
      makeHistoryPoint('2026-08-09T10:00:00.000Z', 12, 2500),
      makeHistoryPoint('2026-08-09T11:00:00.000Z', 13, 2600),
    ], '24h', null, { currentSessionHasCandles: true });

    expect(data).toHaveLength(5);
    expect(data[0]).toMatchObject({ closeChart: 10, volumeChart: 1000 });
    expect(data[1]).toMatchObject({ closeChart: 11, volumeChart: 1200 });
    expect(data[2]).toMatchObject({ closeChart: null, volumeChart: null, isGapMarker: true });
    expect(data[3]).toMatchObject({ closeChart: 12, volumeChart: 2500 });
    expect(data[4]).toMatchObject({ closeChart: 13, volumeChart: 2600 });
  });

  it('keeps only a bounded real tail from the previous session for 24h', () => {
    const previousSessionPoints = Array.from({ length: PREVIOUS_SESSION_TAIL_MAX_POINTS + 2 }, (_, index) =>
      makeHistoryPoint(`2026-08-20T${String(9 + index).padStart(2, '0')}:00:00.000Z`, 100 + index, 1000 + index));
    const currentSessionPoints = [
      makeHistoryPoint('2026-08-21T08:00:00.000Z', 200, 2000),
      makeHistoryPoint('2026-08-21T09:00:00.000Z', 201, 2100),
    ];

    const data = buildHistoryChartData([...previousSessionPoints, ...currentSessionPoints], '24h', null, {
      currentSessionHasCandles: true,
    });

    expect(data.filter((point) => point.isGapMarker)).toHaveLength(1);
    const nonGapPoints = data.filter((point) => point.isGapMarker !== true);
    expect(nonGapPoints).toHaveLength(PREVIOUS_SESSION_TAIL_MAX_POINTS + currentSessionPoints.length);
    expect(nonGapPoints[0]?.timestamp).toBe(previousSessionPoints[2].timestamp);
    expect(nonGapPoints[PREVIOUS_SESSION_TAIL_MAX_POINTS - 1]?.timestamp)
      .toBe(previousSessionPoints[previousSessionPoints.length - 1].timestamp);
    expect(nonGapPoints.slice(-currentSessionPoints.length).map((point) => point.timestamp))
      .toEqual(currentSessionPoints.map((point) => point.timestamp));
  });

  it('does not fabricate a previous-session tail when only one session is available', () => {
    const singleSession = [
      makeHistoryPoint('2026-08-21T08:00:00.000Z', 200, 2000),
      makeHistoryPoint('2026-08-21T09:00:00.000Z', 201, 2100),
      makeHistoryPoint('2026-08-21T10:00:00.000Z', 202, 2200),
    ];

    const data = buildHistoryChartData(singleSession, 'today', null, { currentSessionHasCandles: false });

    expect(data).toHaveLength(singleSession.length);
    expect(data.every((point) => point.isGapMarker !== true)).toBe(true);
    expect(data.map((point) => point.timestamp)).toEqual(singleSession.map((point) => point.timestamp));
  });

  describe('comparePreviousCloseToHistoryEndpoint', () => {
    it('treats close-enough values as matching using absolute/relative tolerance', () => {
      const diagnostics = comparePreviousCloseToHistoryEndpoint(282, 282.01);

      expect(diagnostics.comparable).toBe(true);
      expect(diagnostics.matchesWithinTolerance).toBe(true);
      expect(diagnostics.tolerance).toBeGreaterThanOrEqual(PREVIOUS_CLOSE_MISMATCH_ABSOLUTE_TOLERANCE);
      expect(diagnostics.tolerance).toBeGreaterThanOrEqual(282 * PREVIOUS_CLOSE_MISMATCH_RELATIVE_TOLERANCE);
    });

    it('flags material mismatches', () => {
      const diagnostics = comparePreviousCloseToHistoryEndpoint(282, 273.4);

      expect(diagnostics.comparable).toBe(true);
      expect(diagnostics.matchesWithinTolerance).toBe(false);
      expect(diagnostics.absoluteDifference).toBeCloseTo(8.6, 10);
    });

    it('skips comparison when any value is invalid or non-positive', () => {
      expect(comparePreviousCloseToHistoryEndpoint(null, 100).comparable).toBe(false);
      expect(comparePreviousCloseToHistoryEndpoint(100, 0).comparable).toBe(false);
      expect(comparePreviousCloseToHistoryEndpoint(Number.NaN, 100).comparable).toBe(false);
    });
  });

  it('preserves today range gap-marker behavior (no display-coordinate rewrite in data builder)', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-20T10:00:00.000Z', 10, 1000),
      makeHistoryPoint('2026-08-20T11:00:00.000Z', 10.5, 1050),
      makeHistoryPoint('2026-08-21T10:00:00.000Z', 11, 1200),
      makeHistoryPoint('2026-08-21T11:00:00.000Z', 11.5, 1300),
    ], 'today');

    expect(data).toHaveLength(5);
    expect(data[2]).toMatchObject({ closeChart: null, isGapMarker: true });
    expect(data.every((point) => point.displayX === undefined)).toBe(true);
  });

  it('maps current-session layout to ~10% tail / 2% gap / 88% primary when current session has candles', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-20T14:00:00.000Z', 99, 900),
      makeHistoryPoint('2026-08-20T16:00:00.000Z', 100, 1100),
      makeHistoryPoint('2026-08-21T08:05:00.000Z', 101, 1200),
      makeHistoryPoint('2026-08-21T09:05:00.000Z', 103, 1300),
    ], '24h', null, { currentSessionHasCandles: true });

    const compressed = compressIntradaySessionGaps(data, 1200);

    const gapMarkerIndex = compressed.findIndex((point) => point.isGapMarker === true);
    expect(gapMarkerIndex).toBeGreaterThan(0);
    const previousTail = compressed.slice(0, gapMarkerIndex);
    const marker = compressed[gapMarkerIndex]!;
    const primarySession = compressed.slice(gapMarkerIndex + 1);

    const previousWidth = previousTail[previousTail.length - 1].displayX - previousTail[0].displayX;
    const primaryWidth = primarySession[primarySession.length - 1].displayX - primarySession[0].displayX;
    const gapWidth = primarySession[0].displayX - marker.displayX;

    expect(previousTail.every((point) => point.sessionRole === 'previous-tail')).toBe(true);
    expect(primarySession.every((point) => point.sessionRole === 'primary')).toBe(true);
    expect(previousWidth).toBeCloseTo(PREVIOUS_SESSION_DISPLAY_RATIO, 3);
    expect(gapWidth).toBeCloseTo(SESSION_GAP_DISPLAY_RATIO / 2, 3);
    expect(primaryWidth).toBeCloseTo(PRIMARY_SESSION_DISPLAY_RATIO, 3);
    expect(primarySession[0].displayX).toBeCloseTo(PREVIOUS_SESSION_DISPLAY_RATIO + SESSION_GAP_DISPLAY_RATIO, 3);
    expect(primarySession[primarySession.length - 1].displayX).toBeCloseTo(1, 6);
  });

  it('uses latest completed real session as primary when currentSessionHasCandles=false', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-19T14:00:00.000Z', 90, 900),
      makeHistoryPoint('2026-08-19T16:00:00.000Z', 91, 920),
      makeHistoryPoint('2026-08-20T14:00:00.000Z', 99, 990),
      makeHistoryPoint('2026-08-20T16:00:00.000Z', 100, 1100),
      { ...makeHistoryPoint('2026-08-21T08:05:00.000Z', 250, 0), isQuoteDerived: true },
    ], 'today', null, { currentSessionHasCandles: false });

    const nonGapPoints = data.filter((point) => point.isGapMarker !== true);
    expect(nonGapPoints.some((point) => point.timestamp === '2026-08-21T08:05:00.000Z')).toBe(false);
    expect(nonGapPoints.filter((point) => point.sessionRole === 'primary').map((point) => point.timestamp)).toEqual([
      '2026-08-20T14:00:00.000Z',
      '2026-08-20T16:00:00.000Z',
    ]);
    expect(nonGapPoints.filter((point) => point.sessionRole === 'previous-tail').map((point) => point.timestamp)).toEqual([
      '2026-08-19T14:00:00.000Z',
      '2026-08-19T16:00:00.000Z',
    ]);
  });

  it('uses full width when only one real intraday session is available', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-21T08:00:00.000Z', 200, 2000),
      makeHistoryPoint('2026-08-21T09:00:00.000Z', 201, 2100),
      { ...makeHistoryPoint('2026-08-21T10:00:00.000Z', 202, 2200), isQuoteDerived: true },
    ], 'today', null, { currentSessionHasCandles: false });

    expect(data.every((point) => point.isGapMarker !== true)).toBe(true);
    expect(data.every((point) => point.sessionRole === 'primary')).toBe(true);

    const compressed = compressIntradaySessionGaps(data, 1200);
    expect(compressed[0].displayX).toBeCloseTo(0, 6);
    expect(compressed[compressed.length - 1].displayX).toBeCloseTo(1, 6);
    expect(compressed.every((point) => point.isGapMarker !== true)).toBe(true);
  });

  it('keeps price and volume points aligned on the same normalized displayX coordinates', () => {
    const data = buildHistoryChartData([
      makeHistoryPoint('2026-08-20T14:00:00.000Z', 99, 900),
      makeHistoryPoint('2026-08-20T16:00:00.000Z', 100, 1100),
      makeHistoryPoint('2026-08-21T08:05:00.000Z', 101, 1200),
    ], '24h', null, { currentSessionHasCandles: true });

    const compressedWide = compressIntradaySessionGaps(data, 1200);
    const compressedNarrow = compressIntradaySessionGaps(data, 800);

    expect(compressedWide.map((point) => point.timestamp)).toEqual(compressedNarrow.map((point) => point.timestamp));
    compressedWide.forEach((point, index) => {
      expect(point.displayX).toBeCloseTo(compressedNarrow[index].displayX, 6);
      if (point.isGapMarker !== true) {
        expect(point.closeChart == null).toBe(false);
      }
    });
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
