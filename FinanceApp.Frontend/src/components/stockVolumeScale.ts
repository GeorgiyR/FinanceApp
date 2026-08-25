import type { StockHistoryRange } from '../types';

const FRANKFURT_EXCHANGE = 'frankfurt';
const FRANKFURT_LONG_RANGE_RECENT_SAMPLE_SIZE = 20;
const FRANKFURT_LONG_RANGE_HEADROOM_MULTIPLIER = 1.1;
const FRANKFURT_LONG_RANGE_RECENT_MEDIAN_MULTIPLIER = 2.2;
const FRANKFURT_LONG_RANGE_RECENT_P75_MULTIPLIER = 1.7;
const FRANKFURT_LONG_RANGE_FULL_P35_MULTIPLIER = 1.8;
// Require enough positive points so weekly/monthly long-range series do not overreact to tiny samples.
const MIN_SKEW_SAMPLE_SIZE = 5;
// Frankfurt adaptation should trigger either on one dominant outlier or on a heavy upper-tail cluster.
const MAX_TO_MEDIAN_ACTIVATION_THRESHOLD = 8;
const P95_TO_MEDIAN_ACTIVATION_THRESHOLD = 6;
// Keep headroom for visual continuity while still making ordinary bars materially visible.
const ROBUST_HEADROOM_MULTIPLIER = 1.1;
const ROBUST_P75_MULTIPLIER = 2;
const ROBUST_MEDIAN_MULTIPLIER = 4;
// Avoid enabling adaptive mode when the robust and actual bounds are too close to matter visually.
const MIN_RELATIVE_SEPARATION = 0.12;
const MIN_ABSOLUTE_SEPARATION_TO_MEDIAN = 2;

const isFiniteNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);

const toPositiveFiniteVolumes = (volumes: Array<number | null | undefined>): number[] =>
  volumes.filter((value): value is number => isFiniteNumber(value) && value > 0);

const lowerQuantile = (sorted: number[], percentile: number): number => {
  if (sorted.length === 0) {
    return 0;
  }
  const clamped = Math.min(1, Math.max(0, percentile));
  const index = Math.floor((sorted.length - 1) * clamped);
  return sorted[index];
};

export const isFrankfurtListing = (exchange: string | null | undefined): boolean =>
  exchange?.trim().toLowerCase() === FRANKFURT_EXCHANGE;

export type VolumeScaleAnalysis = {
  hasPositiveFiniteVolume: boolean;
  adaptiveScaleActive: boolean;
  actualUpperBound: number | null;
  displayUpperBound: number | null;
  sampleSize: number;
  median: number | null;
  p75: number | null;
  p95: number | null;
  activationReason: 'maxToMedian' | 'p95ToMedian' | 'combined' | 'deterministicFrankfurtLongRange' | null;
};

type VolumeScaleContext = {
  historyRange?: StockHistoryRange | null;
  interval?: string | null;
};

const getRecentPositiveFiniteVolumes = (
  volumes: Array<number | null | undefined>,
  sampleSize: number,
): number[] => {
  const recent: number[] = [];
  for (let index = volumes.length - 1; index >= 0 && recent.length < sampleSize; index -= 1) {
    const value = volumes[index];
    if (isFiniteNumber(value) && value > 0) {
      recent.push(value);
    }
  }
  return recent;
};

const isFrankfurtLongRangeWithExpectedCadence = (
  exchange: string | null | undefined,
  historyRange: StockHistoryRange | null | undefined,
  interval: string | null | undefined,
): boolean => {
  if (!isFrankfurtListing(exchange)) {
    return false;
  }
  if (historyRange !== '1y' && historyRange !== '3y' && historyRange !== '5y') {
    return false;
  }
  const cadence = getCadenceFromInterval(interval);
  if (historyRange === '1y') {
    return cadence === 'weekly';
  }
  return cadence === 'monthly';
};

export const analyzeAdaptiveVolumeScale = (
  exchange: string | null | undefined,
  volumes: Array<number | null | undefined>,
  context?: VolumeScaleContext,
): VolumeScaleAnalysis => {
  const positiveVolumes = toPositiveFiniteVolumes(volumes).sort((left, right) => left - right);
  if (positiveVolumes.length === 0) {
    return {
      hasPositiveFiniteVolume: false,
      adaptiveScaleActive: false,
      actualUpperBound: null,
      displayUpperBound: null,
      sampleSize: 0,
      median: null,
      p75: null,
      p95: null,
      activationReason: null,
    };
  }

  const sampleSize = positiveVolumes.length;
  const actualUpperBound = positiveVolumes[positiveVolumes.length - 1];
  const median = lowerQuantile(positiveVolumes, 0.5);
  const p75 = lowerQuantile(positiveVolumes, 0.75);
  const p95 = lowerQuantile(positiveVolumes, 0.95);

  if (isFrankfurtLongRangeWithExpectedCadence(exchange, context?.historyRange, context?.interval)) {
    const recentPositiveSorted = getRecentPositiveFiniteVolumes(volumes, FRANKFURT_LONG_RANGE_RECENT_SAMPLE_SIZE)
      .sort((left, right) => left - right);
    const recentMedian = lowerQuantile(recentPositiveSorted, 0.5);
    const recentP75 = lowerQuantile(recentPositiveSorted, 0.75);
    const fullP35 = lowerQuantile(positiveVolumes, 0.35);
    const robustBase = Math.max(
      recentMedian * FRANKFURT_LONG_RANGE_RECENT_MEDIAN_MULTIPLIER,
      recentP75 * FRANKFURT_LONG_RANGE_RECENT_P75_MULTIPLIER,
      fullP35 * FRANKFURT_LONG_RANGE_FULL_P35_MULTIPLIER,
    );
    const robustUpperBound = Math.min(
      actualUpperBound,
      robustBase * FRANKFURT_LONG_RANGE_HEADROOM_MULTIPLIER,
    );

    return {
      hasPositiveFiniteVolume: true,
      adaptiveScaleActive: true,
      actualUpperBound,
      displayUpperBound: robustUpperBound,
      sampleSize,
      median,
      p75,
      p95,
      activationReason: 'deterministicFrankfurtLongRange',
    };
  }

  const maxToMedian = median > 0 ? actualUpperBound / median : Number.POSITIVE_INFINITY;
  const p95ToMedian = median > 0 ? p95 / median : Number.POSITIVE_INFINITY;

  const maxSkewSignal = maxToMedian >= MAX_TO_MEDIAN_ACTIVATION_THRESHOLD;
  const p95SkewSignal = p95ToMedian >= P95_TO_MEDIAN_ACTIVATION_THRESHOLD;
  const activationReason =
    maxSkewSignal && p95SkewSignal
      ? 'combined'
      : maxSkewSignal
        ? 'maxToMedian'
        : p95SkewSignal
          ? 'p95ToMedian'
          : null;

  const hasSkewSignal = sampleSize >= MIN_SKEW_SAMPLE_SIZE && median > 0 && activationReason !== null;

  if (!isFrankfurtListing(exchange) || !hasSkewSignal) {
    return {
      hasPositiveFiniteVolume: true,
      adaptiveScaleActive: false,
      actualUpperBound,
      displayUpperBound: actualUpperBound,
      sampleSize,
      median,
      p75,
      p95,
      activationReason: null,
    };
  }

  const robustBase = Math.max(p75 * ROBUST_P75_MULTIPLIER, median * ROBUST_MEDIAN_MULTIPLIER);
  const robustUpperBound = Math.min(actualUpperBound, robustBase * ROBUST_HEADROOM_MULTIPLIER);
  const absoluteSeparation = actualUpperBound - robustUpperBound;
  const relativeSeparation = actualUpperBound > 0 ? absoluteSeparation / actualUpperBound : 0;
  const hasMeaningfulSeparation = absoluteSeparation >= median * MIN_ABSOLUTE_SEPARATION_TO_MEDIAN
    && relativeSeparation >= MIN_RELATIVE_SEPARATION;

  if (!hasMeaningfulSeparation) {
    return {
      hasPositiveFiniteVolume: true,
      adaptiveScaleActive: false,
      actualUpperBound,
      displayUpperBound: actualUpperBound,
      sampleSize,
      median,
      p75,
      p95,
      activationReason: null,
    };
  }

  return {
    hasPositiveFiniteVolume: true,
    adaptiveScaleActive: true,
    actualUpperBound,
    displayUpperBound: robustUpperBound,
    sampleSize,
    median,
    p75,
    p95,
    activationReason,
  };
};

export const toDisplayVolume = (
  actualVolume: number | null | undefined,
  analysis: VolumeScaleAnalysis,
): { displayVolume: number | null; volumeCapped: boolean } => {
  if (!isFiniteNumber(actualVolume)) {
    return { displayVolume: null, volumeCapped: false };
  }
  if (!analysis.adaptiveScaleActive || analysis.displayUpperBound == null || actualVolume <= 0) {
    return { displayVolume: actualVolume, volumeCapped: false };
  }
  if (actualVolume > analysis.displayUpperBound) {
    return { displayVolume: analysis.displayUpperBound, volumeCapped: true };
  }
  return { displayVolume: actualVolume, volumeCapped: false };
};

export const formatVolumeTooltipValue = (actualVolume: number, volumeCapped: boolean): string => {
  const volumeText = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 0 }).format(actualVolume);
  return volumeCapped
    ? `${volumeText} (выброс; визуально ограничен)`
    : volumeText;
};

const getCadenceFromInterval = (interval: string | null | undefined): 'weekly' | 'monthly' | null => {
  if (!interval) {
    return null;
  }
  const normalized = interval.trim().toLowerCase();
  if (normalized.includes('wk') || normalized.includes('week')) {
    return 'weekly';
  }
  if (normalized.includes('mo') || normalized.includes('month')) {
    return 'monthly';
  }
  return null;
};

export const getVolumeCadenceHint = (
  historyRange: StockHistoryRange,
  interval: string | null | undefined,
): string | null => {
  if (historyRange !== '1y' && historyRange !== '3y' && historyRange !== '5y') {
    return null;
  }
  const intervalCadence = getCadenceFromInterval(interval);
  if (intervalCadence === 'weekly') {
    return 'Объём по недельным свечам.';
  }
  if (intervalCadence === 'monthly') {
    return 'Объём по месячным свечам.';
  }
  if (historyRange === '1y') {
    return 'Объём по недельным свечам.';
  }
  return 'Объём по месячным свечам.';
};
