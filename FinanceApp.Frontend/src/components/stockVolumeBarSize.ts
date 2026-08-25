import type { StockHistoryRange } from '../types';

const LONG_RANGE_WITH_TIME_AXIS = new Set<StockHistoryRange>(['1y', '3y', '5y']);

const DEFAULT_CHART_WIDTH_PX = 960;
const APPROX_NON_PLOT_WIDTH_PX = 76;
const BAR_TO_SLOT_RATIO = 0.72;

export const MIN_VOLUME_BAR_SIZE_PX = 4;
export const MAX_VOLUME_BAR_SIZE_PX = 14;

export const resolveVolumeBarSize = ({
  containerWidth,
  positivePointCount,
  historyRange,
}: {
  containerWidth: number;
  positivePointCount: number;
  historyRange: StockHistoryRange;
}): number | undefined => {
  if (!LONG_RANGE_WITH_TIME_AXIS.has(historyRange) || positivePointCount <= 0) {
    return undefined;
  }

  const effectiveContainerWidth = containerWidth > 0 ? containerWidth : DEFAULT_CHART_WIDTH_PX;
  const plotWidth = Math.max(1, effectiveContainerWidth - APPROX_NON_PLOT_WIDTH_PX);
  const slotWidth = plotWidth / positivePointCount;
  const sizedWidth = Math.round(slotWidth * BAR_TO_SLOT_RATIO);

  return Math.min(MAX_VOLUME_BAR_SIZE_PX, Math.max(MIN_VOLUME_BAR_SIZE_PX, sizedWidth));
};
