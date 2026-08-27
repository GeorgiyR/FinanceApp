import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
import type { StockHistoryPoint, StockHistoryRange } from '../types';

dayjs.extend(utc);

const SHORT_INTRADAY_GAP_THRESHOLD_MS = 2 * 60 * 60 * 1000;
const MIN_GAP_MARKER_OFFSET_MS = 1;
export const PREVIOUS_SESSION_DISPLAY_RATIO = 0.10;
export const SESSION_GAP_DISPLAY_RATIO = 0.02;
export const PRIMARY_SESSION_DISPLAY_RATIO = 0.88;
export const PREVIOUS_CLOSE_MISMATCH_ABSOLUTE_TOLERANCE = 0.02;
export const PREVIOUS_CLOSE_MISMATCH_RELATIVE_TOLERANCE = 0.001;
export const PREVIOUS_SESSION_TAIL_MAX_POINTS = 6;

const historyGapThresholdMsByRange: Partial<Record<StockHistoryRange, number>> = {
  '24h': SHORT_INTRADAY_GAP_THRESHOLD_MS,
  today: SHORT_INTRADAY_GAP_THRESHOLD_MS,
};
const INTRADAY_SESSION_TAIL_RANGE_SET = new Set<StockHistoryRange>(['24h', 'today']);

export type HistoryChartPoint = {
  timestamp: string;
  timestampMs: number;
  displayX?: number;
  closeChart: number | null;
  rawClose: number;
  volumeChart: number | null;
  volumeDisplay?: number | null;
  volumeCapped?: boolean;
  isQuoteDerived?: boolean;
  isGapMarker?: boolean;
  sessionRole?: 'previous-tail' | 'primary';
  chartIndex?: number;
};

export type CurrentQuoteOverlayPoint = {
  timestampUtc: string | null | undefined;
  closeChart: number | null | undefined;
  rawClose?: number | null | undefined;
  isStale?: boolean | null;
  isForRequestedInstrument?: boolean | null;
};

export interface PreviousCloseMatchDiagnostics {
  comparable: boolean;
  matchesWithinTolerance: boolean;
  absoluteDifference: number | null;
  tolerance: number | null;
}

const DATE_ONLY_HISTORY_RANGE_SET = new Set<StockHistoryRange>(['5y', '3y', '1y', '6m', '3m', '1m']);
const SHORT_DAILY_HISTORY_RANGE_SET = new Set<StockHistoryRange>(['6m', '3m', '1m']);
const APPEND_CURRENT_POINT_HISTORY_RANGE_SET = new Set<StockHistoryRange>(['1y', '6m', '3m', '1m']);

const isFiniteNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);

const isPositiveFiniteNumber = (value: unknown): value is number =>
  isFiniteNumber(value) && value > 0;

export const comparePreviousCloseToHistoryEndpoint = (
  previousCloseValue: number | null | undefined,
  historyEndpointValue: number | null | undefined,
): PreviousCloseMatchDiagnostics => {
  if (!isPositiveFiniteNumber(previousCloseValue) || !isPositiveFiniteNumber(historyEndpointValue)) {
    return {
      comparable: false,
      matchesWithinTolerance: false,
      absoluteDifference: null,
      tolerance: null,
    };
  }

  const absoluteDifference = Math.abs(previousCloseValue - historyEndpointValue);
  const tolerance = Math.max(
    PREVIOUS_CLOSE_MISMATCH_ABSOLUTE_TOLERANCE,
    previousCloseValue * PREVIOUS_CLOSE_MISMATCH_RELATIVE_TOLERANCE,
    historyEndpointValue * PREVIOUS_CLOSE_MISMATCH_RELATIVE_TOLERANCE,
  );

  return {
    comparable: true,
    matchesWithinTolerance: absoluteDifference <= tolerance,
    absoluteDifference,
    tolerance,
  };
};

export const usesUtcDateLabels = (historyRange: StockHistoryRange): boolean =>
  DATE_ONLY_HISTORY_RANGE_SET.has(historyRange);

export const formatHistoryTimestamp = (
  timestamp: string | number,
  historyRange: StockHistoryRange,
  format: string,
): string => {
  const parsed = dayjs.utc(timestamp);
  return usesUtcDateLabels(historyRange)
    ? parsed.format(format)
    : parsed.local().format(format);
};

const getEffectiveHistoryDateKey = (
  timestamp: string,
  historyRange: StockHistoryRange,
): string => formatHistoryTimestamp(timestamp, historyRange, 'YYYY-MM-DD');

const splitByGapThreshold = (
  sortedPoints: HistoryChartPoint[],
  historyRange: StockHistoryRange,
): HistoryChartPoint[][] => {
  const gapThresholdMs = historyGapThresholdMsByRange[historyRange];
  if (
    !INTRADAY_SESSION_TAIL_RANGE_SET.has(historyRange)
    || !gapThresholdMs
    || sortedPoints.length === 0
  ) {
    return sortedPoints.length > 0 ? [sortedPoints] : [];
  }

  const sessions: HistoryChartPoint[][] = [];
  let currentSession: HistoryChartPoint[] = [sortedPoints[0]];
  for (let i = 1; i < sortedPoints.length; i += 1) {
    if (sortedPoints[i].timestampMs - sortedPoints[i - 1].timestampMs > gapThresholdMs) {
      sessions.push(currentSession);
      currentSession = [];
    }
    currentSession.push(sortedPoints[i]);
  }
  sessions.push(currentSession);
  return sessions;
};

const selectIntradaySessionsForLayout = (
  sessions: HistoryChartPoint[][],
  currentSessionHasCandles?: boolean | null,
): { previousTail: HistoryChartPoint[]; primarySession: HistoryChartPoint[] } => {
  if (sessions.length === 0) {
    return { previousTail: [], primarySession: [] };
  }

  const primarySessionIndex = currentSessionHasCandles === false
    ? sessions.length - 1
    : sessions.length - 1;
  const primarySession = sessions[primarySessionIndex];
  if (primarySession == null || primarySession.length === 0) {
    return { previousTail: [], primarySession: [] };
  }

  const previousSession = sessions[primarySessionIndex - 1] ?? [];
  const previousSessionTail = previousSession.length >= 2
    ? previousSession.slice(-PREVIOUS_SESSION_TAIL_MAX_POINTS)
    : [];
  return { previousTail: previousSessionTail, primarySession };
};

export const buildHistoryChartData = (
  historyData: StockHistoryPoint[],
  historyRange: StockHistoryRange,
  currentQuoteOverlay?: CurrentQuoteOverlayPoint | null,
  options?: {
    currentSessionHasCandles?: boolean | null;
  },
): HistoryChartPoint[] => {
  const sortedPoints: HistoryChartPoint[] = historyData
    .map((point) => ({
      timestamp: point.timestamp,
      timestampMs: dayjs.utc(point.timestamp).valueOf(),
      closeChart: point.closeEur ?? point.closeNormalized,
      rawClose: point.closeRaw,
      volumeChart: point.volume,
      volumeDisplay: point.volume,
      volumeCapped: false,
      isQuoteDerived: point.isQuoteDerived ?? false,
    }))
    .sort((left, right) => left.timestampMs - right.timestampMs);

  if (APPEND_CURRENT_POINT_HISTORY_RANGE_SET.has(historyRange)
      && currentQuoteOverlay?.isStale !== true
      && currentQuoteOverlay?.isForRequestedInstrument !== false) {
    const overlayTimestamp = currentQuoteOverlay?.timestampUtc ?? null;
    const overlayClose = currentQuoteOverlay?.closeChart ?? null;
    const overlayTimestampMs = overlayTimestamp ? dayjs.utc(overlayTimestamp).valueOf() : Number.NaN;
    const nowMs = Date.now();
    const latestPoint = sortedPoints[sortedPoints.length - 1];
    const hasSameTradingDay = latestPoint != null
      && SHORT_DAILY_HISTORY_RANGE_SET.has(historyRange)
      && getEffectiveHistoryDateKey(overlayTimestamp ?? '', historyRange) === getEffectiveHistoryDateKey(latestPoint.timestamp, historyRange);

    if (
      latestPoint != null
      && overlayTimestamp != null
      && Number.isFinite(overlayTimestampMs)
      && overlayTimestampMs <= nowMs
      && isFiniteNumber(overlayClose)
      && overlayTimestampMs > latestPoint.timestampMs
      && !hasSameTradingDay
    ) {
      sortedPoints.push({
        timestamp: overlayTimestamp,
        timestampMs: overlayTimestampMs,
        closeChart: overlayClose,
        rawClose: currentQuoteOverlay?.rawClose ?? overlayClose,
        volumeChart: null,
        volumeDisplay: null,
        volumeCapped: false,
        isQuoteDerived: false,
      });
    }
  }

  if (historyRange === '1w') {
    return sortedPoints.map((pt, idx) => ({ ...pt, chartIndex: idx }));
  }

  const currentSessionHasCandles = options?.currentSessionHasCandles ?? null;
  const realProviderPoints = (historyRange === '24h' || historyRange === 'today')
    ? sortedPoints.filter((point) => point.isQuoteDerived !== true)
    : sortedPoints;
  const sessionScopedPoints = (() => {
    if (!(historyRange === '24h' || historyRange === 'today')) {
      return realProviderPoints;
    }

    const sessions = splitByGapThreshold(realProviderPoints, historyRange);
    const { previousTail, primarySession } = selectIntradaySessionsForLayout(
      sessions,
      currentSessionHasCandles,
    );
    const fallbackPrimarySeries = realProviderPoints.map((point) => ({ ...point, sessionRole: 'primary' as const }));
    if (primarySession.length < 2 && sessions.length > 2) {
      return fallbackPrimarySeries;
    }

    const previousTailWithRole = previousTail.map((point) => ({ ...point, sessionRole: 'previous-tail' as const }));
    const primarySessionWithRole = primarySession.map((point) => ({ ...point, sessionRole: 'primary' as const }));

    if (previousTailWithRole.length < 2 || primarySessionWithRole.length === 0) {
      return primarySessionWithRole.length > 0 ? primarySessionWithRole : fallbackPrimarySeries;
    }

    return [...previousTailWithRole, ...primarySessionWithRole];
  })();
  const gapThresholdMs = historyGapThresholdMsByRange[historyRange];
  if (!gapThresholdMs || sessionScopedPoints.length < 2) {
    return sessionScopedPoints;
  }

  const pointsWithGaps: HistoryChartPoint[] = [sessionScopedPoints[0]];
  let previousPoint = sessionScopedPoints[0];
  for (let i = 1; i < sessionScopedPoints.length; i += 1) {
    const currentPoint = sessionScopedPoints[i];
    const gapMs = currentPoint.timestampMs - previousPoint.timestampMs;
    if (gapMs > gapThresholdMs) {
      const gapTimestampMs = previousPoint.timestampMs + MIN_GAP_MARKER_OFFSET_MS;
      pointsWithGaps.push({
        timestamp: new Date(gapTimestampMs).toISOString(),
        timestampMs: gapTimestampMs,
        closeChart: null,
        rawClose: previousPoint.rawClose,
        volumeChart: null,
        volumeDisplay: null,
        volumeCapped: false,
        isGapMarker: true,
      });
    }
    pointsWithGaps.push(currentPoint);
    previousPoint = currentPoint;
  }

  return pointsWithGaps;
};

export const compressIntradaySessionGaps = <T extends {
  timestampMs: number;
  isGapMarker?: boolean;
  sessionRole?: 'previous-tail' | 'primary';
}>(
  points: T[],
  _plotWidthPx: number,
): Array<T & { displayX: number }> => {
  if (points.length === 0) {
    return [];
  }

  if (points.length === 1) {
    return points.map((point) => ({ ...point, displayX: 0 }));
  }

  const mapSegment = (segment: T[], start: number, end: number): Array<T & { displayX: number }> => {
    if (segment.length === 0) {
      return [];
    }
    if (segment.length === 1) {
      return [{ ...segment[0], displayX: start }];
    }
    const firstTs = segment[0].timestampMs;
    const lastTs = segment[segment.length - 1].timestampMs;
    const span = Math.max(0, lastTs - firstTs);
    return segment.map((point, index) => {
      const normalized = span > 0
        ? (point.timestampMs - firstTs) / span
        : index / (segment.length - 1);
      return {
        ...point,
        displayX: start + normalized * (end - start),
      };
    });
  };

  const gapMarkerIndex = points.findIndex((point) => point.isGapMarker === true);
  if (gapMarkerIndex <= 0 || gapMarkerIndex >= points.length - 1) {
    return mapSegment(points, 0, 1);
  }

  const previousTail = points.slice(0, gapMarkerIndex).filter((point) => point.isGapMarker !== true);
  const primarySession = points.slice(gapMarkerIndex + 1).filter((point) => point.isGapMarker !== true);
  if (previousTail.length < 2 || primarySession.length === 0) {
    return mapSegment(primarySession.length > 0 ? primarySession : previousTail, 0, 1);
  }

  const previousMapped = mapSegment(previousTail, 0, PREVIOUS_SESSION_DISPLAY_RATIO);
  const primaryStart = PREVIOUS_SESSION_DISPLAY_RATIO + SESSION_GAP_DISPLAY_RATIO;
  const primaryEnd = primaryStart + PRIMARY_SESSION_DISPLAY_RATIO;
  const primaryMapped = mapSegment(primarySession, primaryStart, primaryEnd);
  const gapMarker = {
    ...points[gapMarkerIndex],
    displayX: PREVIOUS_SESSION_DISPLAY_RATIO + SESSION_GAP_DISPLAY_RATIO / 2,
  };

  return [...previousMapped, gapMarker, ...primaryMapped];
};

export const resolveTimestampMsForDisplayX = <T extends {
  timestampMs: number;
  displayX?: number;
  isGapMarker?: boolean;
}>(
  points: T[],
  displayX: number,
): number | null => {
  let nearestTimestampMs: number | null = null;
  let nearestDistance = Number.POSITIVE_INFINITY;

  for (const point of points) {
    if (point.isGapMarker) {
      continue;
    }
    if (!Number.isFinite(point.displayX)) {
      continue;
    }

    const distance = Math.abs((point.displayX ?? 0) - displayX);
    if (distance < nearestDistance) {
      nearestDistance = distance;
      nearestTimestampMs = point.timestampMs;
    }
  }

  return nearestTimestampMs;
};
