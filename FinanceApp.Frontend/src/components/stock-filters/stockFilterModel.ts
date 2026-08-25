export type AdvancedExchangeFilter = 'fra' | 'us';

export interface AdvancedStockFilters {
  exchanges: AdvancedExchangeFilter[];
  sectorIds: number[];
  industryIds: number[];
}

export const EMPTY_ADVANCED_STOCK_FILTERS: AdvancedStockFilters = {
  exchanges: [],
  sectorIds: [],
  industryIds: [],
};

const sortNumbers = (values: readonly number[]): number[] => [...values].sort((a, b) => a - b);

const uniqueNumbers = (values: readonly number[]): number[] => {
  const seen = new Set<number>();
  const result: number[] = [];
  for (const value of values) {
    if (!Number.isInteger(value) || value <= 0 || seen.has(value)) {
      continue;
    }
    seen.add(value);
    result.push(value);
  }

  return sortNumbers(result);
};

export const EXCHANGE_FILTER_ORDER: readonly AdvancedExchangeFilter[] = ['fra', 'us'];

const uniqueExchanges = (values: readonly AdvancedExchangeFilter[]): AdvancedExchangeFilter[] => {
  const allowed = new Set<AdvancedExchangeFilter>(EXCHANGE_FILTER_ORDER);
  const seen = new Set<AdvancedExchangeFilter>();
  for (const value of values) {
    if (!allowed.has(value) || seen.has(value)) {
      continue;
    }
    seen.add(value);
  }

  return EXCHANGE_FILTER_ORDER.filter((value) => seen.has(value));
};

export const normalizeAdvancedStockFilters = (filters: Partial<AdvancedStockFilters> | null | undefined): AdvancedStockFilters => ({
  exchanges: uniqueExchanges(filters?.exchanges ?? []),
  sectorIds: uniqueNumbers(filters?.sectorIds ?? []),
  industryIds: uniqueNumbers(filters?.industryIds ?? []),
});

export const areAdvancedStockFiltersEqual = (left: AdvancedStockFilters, right: AdvancedStockFilters): boolean => (
  left.exchanges.length === right.exchanges.length
  && left.sectorIds.length === right.sectorIds.length
  && left.industryIds.length === right.industryIds.length
  && left.exchanges.every((value, index) => right.exchanges[index] === value)
  && left.sectorIds.every((value, index) => right.sectorIds[index] === value)
  && left.industryIds.every((value, index) => right.industryIds[index] === value)
);

export const countActiveAdvancedFilterGroups = (filters: AdvancedStockFilters): number => {
  let count = 0;
  if (filters.exchanges.length > 0) count += 1;
  if (filters.sectorIds.length > 0) count += 1;
  if (filters.industryIds.length > 0) count += 1;
  return count;
};

export const hasActiveAdvancedFilters = (filters: AdvancedStockFilters): boolean =>
  countActiveAdvancedFilterGroups(filters) > 0;
