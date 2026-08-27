import type { StockHistoryRange, StockQuoteResponse } from '../types';

export const PREVIOUS_CLOSE_CHANGE_HEADING = 'Изменение к предыдущему закрытию';
export const SELECTED_PERIOD_CHANGE_HEADING = 'Изменение от начала периода';

type SummaryLiveQuote = Pick<
  StockQuoteResponse,
  'currentPriceEur' | 'changeEur' | 'normalizedCurrentPrice' | 'normalizedChange' | 'normalizedPreviousClose'
>;

export interface StockPriceChartSummaryInput {
  historyRange: StockHistoryRange;
  currentPriceDisplayValue: number | null;
  firstHistoryClose: number | null;
  historyHasEurConversion: boolean;
  liveQuote?: SummaryLiveQuote | null;
  storedPriceEur?: number | null;
  storedPriceChangeEur?: number | null;
}

export interface StockPriceChartSummary {
  baselineSource: BaselineSource;
  changeHeading: string;
  baselineValue: number | null;
  changeValue: number | null;
  changePercent: number | null;
}

export type BaselineSource = 'previous-close' | 'selected-period';

const isFiniteNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);

const isPositiveFiniteNumber = (value: unknown): value is number =>
  isFiniteNumber(value) && value > 0;

const getLiveSessionBaseline = (
  liveQuote: SummaryLiveQuote | null | undefined,
  historyHasEurConversion: boolean,
): number | null => {
  if (!liveQuote) {
    return null;
  }

  if (historyHasEurConversion) {
    if (!isFiniteNumber(liveQuote.currentPriceEur) || !isFiniteNumber(liveQuote.changeEur)) {
      return null;
    }

    const previousCloseEur = liveQuote.currentPriceEur - liveQuote.changeEur;
    return isPositiveFiniteNumber(previousCloseEur) ? previousCloseEur : null;
  }

  if (isPositiveFiniteNumber(liveQuote.normalizedPreviousClose)) {
    return liveQuote.normalizedPreviousClose;
  }

  if (!isFiniteNumber(liveQuote.normalizedCurrentPrice) || !isFiniteNumber(liveQuote.normalizedChange)) {
    return null;
  }

  const previousCloseNormalized = liveQuote.normalizedCurrentPrice - liveQuote.normalizedChange;
  return isPositiveFiniteNumber(previousCloseNormalized) ? previousCloseNormalized : null;
};

const getStoredSessionBaseline = (
  storedPriceEur: number | null | undefined,
  storedPriceChangeEur: number | null | undefined,
  historyHasEurConversion: boolean,
): number | null => {
  if (!historyHasEurConversion || !isFiniteNumber(storedPriceEur) || !isFiniteNumber(storedPriceChangeEur)) {
    return null;
  }

  const previousCloseEur = storedPriceEur - storedPriceChangeEur;
  return isPositiveFiniteNumber(previousCloseEur) ? previousCloseEur : null;
};

export const getStockPriceChartSummary = ({
  historyRange,
  currentPriceDisplayValue,
  firstHistoryClose,
  historyHasEurConversion,
  liveQuote,
  storedPriceEur,
  storedPriceChangeEur,
}: StockPriceChartSummaryInput): StockPriceChartSummary => {
  const fallbackBaseline = isFiniteNumber(firstHistoryClose) ? firstHistoryClose : null;
  let baselineValue = fallbackBaseline;
  let baselineSource: BaselineSource = 'selected-period';

  if (historyRange === '24h' || historyRange === 'today') {
    const sessionBaseline =
      getLiveSessionBaseline(liveQuote, historyHasEurConversion)
      ?? getStoredSessionBaseline(storedPriceEur, storedPriceChangeEur, historyHasEurConversion);

    if (sessionBaseline != null) {
      baselineValue = sessionBaseline;
      baselineSource = 'previous-close';
    }
  }

  const changeHeading = baselineSource === 'previous-close'
    ? PREVIOUS_CLOSE_CHANGE_HEADING
    : SELECTED_PERIOD_CHANGE_HEADING;

  const normalizedCurrentPrice = isFiniteNumber(currentPriceDisplayValue) ? currentPriceDisplayValue : null;
  const changeValue = normalizedCurrentPrice != null && baselineValue != null
    ? normalizedCurrentPrice - baselineValue
    : null;
  const changePercent = changeValue != null && baselineValue != null && baselineValue !== 0
    ? (changeValue / baselineValue) * 100
    : null;

  return {
    baselineSource,
    changeHeading,
    baselineValue,
    changeValue,
    changePercent,
  };
};
