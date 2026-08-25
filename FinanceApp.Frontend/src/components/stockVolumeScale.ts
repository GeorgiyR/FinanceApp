import type { StockHistoryRange } from '../types';

const FRANKFURT_EXCHANGE = 'frankfurt';
const MIN_SKEW_SAMPLE_SIZE = 4;
const MAX_TO_MEDIAN_SKEW_THRESHOLD = 12;
const MAX_TO_P95_SKEW_THRESHOLD = 2;
const ROBUST_PERCENTILE = 0.95;
const ROBUST_HEADROOM_MULTIPLIER = 1.15;

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
};

export const analyzeAdaptiveVolumeScale = (
  exchange: string | null | undefined,
  volumes: Array<number | null | undefined>,
): VolumeScaleAnalysis => {
  const positiveVolumes = toPositiveFiniteVolumes(volumes).sort((left, right) => left - right);
  if (positiveVolumes.length === 0) {
    return {
      hasPositiveFiniteVolume: false,
      adaptiveScaleActive: false,
      actualUpperBound: null,
      displayUpperBound: null,
    };
  }

  const actualUpperBound = positiveVolumes[positiveVolumes.length - 1];
  const median = lowerQuantile(positiveVolumes, 0.5);
  const p95 = lowerQuantile(positiveVolumes, ROBUST_PERCENTILE);
  const maxToMedian = median > 0 ? actualUpperBound / median : Number.POSITIVE_INFINITY;
  const maxToP95 = p95 > 0 ? actualUpperBound / p95 : Number.POSITIVE_INFINITY;
  const skewedDistribution =
    positiveVolumes.length >= MIN_SKEW_SAMPLE_SIZE
    && median > 0
    && maxToMedian >= MAX_TO_MEDIAN_SKEW_THRESHOLD
    && maxToP95 >= MAX_TO_P95_SKEW_THRESHOLD;

  if (!isFrankfurtListing(exchange) || !skewedDistribution) {
    return {
      hasPositiveFiniteVolume: true,
      adaptiveScaleActive: false,
      actualUpperBound,
      displayUpperBound: actualUpperBound,
    };
  }

  const robustBase = Math.max(median, p95);
  const robustUpperBound = Math.min(
    actualUpperBound,
    Math.max(robustBase * ROBUST_HEADROOM_MULTIPLIER, median),
  );

  return {
    hasPositiveFiniteVolume: true,
    adaptiveScaleActive: true,
    actualUpperBound,
    displayUpperBound: robustUpperBound,
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
