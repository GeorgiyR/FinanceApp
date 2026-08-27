import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
import type { StockHistoryPoint, StockHistoryRange } from '../types';

dayjs.extend(utc);

const SHORT_INTRADAY_GAP_THRESHOLD_MS = 2 * 60 * 60 * 1000;
const MIN_GAP_MARKER_OFFSET_MS = 1;
export const TARGET_INTERSESSION_GAP_CSS_PX = 75.6;
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

const trimToLatestSessionWithPreviousTail = (
  sortedPoints: HistoryChartPoint[],
  historyRange: StockHistoryRange,
): HistoryChartPoint[] => {
  const gapThresholdMs = historyGapThresholdMsByRange[historyRange];
  if (
    !INTRADAY_SESSION_TAIL_RANGE_SET.has(historyRange)
    || !gapThresholdMs
    || sortedPoints.length < 2
  ) {
    return sortedPoints;
  }

  const sessionStartIndices: number[] = [0];
  for (let i = 1; i < sortedPoints.length; i += 1) {
    if (sortedPoints[i].timestampMs - sortedPoints[i - 1].timestampMs > gapThresholdMs) {
      sessionStartIndices.push(i);
    }
  }

  if (sessionStartIndices.length < 2) {
    return sortedPoints;
  }

  const latestSessionStart = sessionStartIndices[sessionStartIndices.length - 1];
  const previousSessionStart = sessionStartIndices[sessionStartIndices.length - 2];
  const previousSessionPoints = sortedPoints.slice(previousSessionStart, latestSessionStart);
  const latestSessionPoints = sortedPoints.slice(latestSessionStart);
  if (previousSessionPoints.length < 2 || latestSessionPoints.length < 2) {
    return sortedPoints;
  }

  const previousSessionTail = previousSessionPoints.slice(-PREVIOUS_SESSION_TAIL_MAX_POINTS);

  return [...previousSessionTail, ...latestSessionPoints];
};

export const buildHistoryChartData = (
  historyData: StockHistoryPoint[],
  historyRange: StockHistoryRange,
  currentQuoteOverlay?: CurrentQuoteOverlayPoint | null,
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

  const sessionScopedPoints = trimToLatestSessionWithPreviousTail(sortedPoints, historyRange);
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
}>(
  points: T[],
  plotWidthPx: number,
  targetGapPx = TARGET_INTERSESSION_GAP_CSS_PX,
): Array<T & { displayX: number }> => {
  if (points.length === 0) {
    return [];
  }

  if (!Number.isFinite(plotWidthPx) || plotWidthPx <= 0 || points.length === 1) {
    return points.map((point, index) => ({ ...point, displayX: index }));
  }

  let breakCount = 0;
  let totalInSessionMs = 0;
  for (let i = 1; i < points.length; i += 1) {
    const previousPoint = points[i - 1];
    const currentPoint = points[i];
    if (previousPoint.isGapMarker) {
      breakCount += 1;
      continue;
    }
    if (currentPoint.isGapMarker) {
      continue;
    }

    totalInSessionMs += Math.max(0, currentPoint.timestampMs - previousPoint.timestampMs);
  }

  if (breakCount === 0) {
    return points.map((point) => ({ ...point, displayX: point.timestampMs }));
  }

  const maxGapSharePx = plotWidthPx * 0.7;
  const gapPx = Math.min(targetGapPx, maxGapSharePx / breakCount);
  const availableInSessionPx = Math.max(plotWidthPx - gapPx * breakCount, plotWidthPx * 0.15);
  const pxPerMs = totalInSessionMs > 0 ? availableInSessionPx / totalInSessionMs : 0;

  const pointsWithDisplay: Array<T & { displayX: number }> = [{ ...points[0], displayX: 0 }];
  for (let i = 1; i < points.length; i += 1) {
    const previousPoint = points[i - 1];
    const currentPoint = points[i];
    const previousDisplayX = pointsWithDisplay[i - 1].displayX;

    let displayDelta = 0;
    if (previousPoint.isGapMarker) {
      displayDelta = gapPx;
    } else if (!currentPoint.isGapMarker) {
      displayDelta = Math.max(0, currentPoint.timestampMs - previousPoint.timestampMs) * pxPerMs;
    }

    pointsWithDisplay.push({
      ...currentPoint,
      displayX: previousDisplayX + displayDelta,
    });
  }

  return pointsWithDisplay;
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
