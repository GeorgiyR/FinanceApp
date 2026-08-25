import type { IndexConstituentDto, SectorDto, Stock } from '../../types';
import type { AdvancedExchangeFilter, AdvancedStockFilters } from './stockFilterModel';

export const normalizeExchangeFilterGroup = (exchange: string | null | undefined): AdvancedExchangeFilter | null => {
  const normalized = (exchange ?? '').trim().toLowerCase();
  if (!normalized) {
    return null;
  }

  if (normalized === 'frankfurt' || normalized === 'fra') {
    return 'fra';
  }

  if (normalized === 'nyse' || normalized === 'nasdaq') {
    return 'us';
  }

  return null;
};

export const buildIndustryToSectorMap = (sectors: readonly SectorDto[]): Map<number, number> => {
  const map = new Map<number, number>();
  for (const sector of sectors) {
    for (const industry of sector.industries) {
      map.set(industry.id, industry.sectorId);
    }
  }

  return map;
};

type FilterCandidate = {
  exchange: string | null | undefined;
  sectorId?: number | null;
  industryId?: number | null;
};

const toId = (value: number | null | undefined): number | null =>
  Number.isInteger(value) && Number(value) > 0 ? Number(value) : null;

const matchesExchangeGroup = (
  exchange: string | null | undefined,
  selected: readonly AdvancedExchangeFilter[],
): boolean => {
  if (selected.length === 0) {
    return true;
  }

  const group = normalizeExchangeFilterGroup(exchange);
  return group != null && selected.includes(group);
};

const matchesIdGroup = (value: number | null, selected: readonly number[]): boolean => {
  if (selected.length === 0) {
    return true;
  }

  return value != null && selected.includes(value);
};

export const matchesAdvancedStockFilters = (
  candidate: FilterCandidate,
  filters: AdvancedStockFilters,
): boolean => matchesExchangeGroup(candidate.exchange, filters.exchanges)
  && matchesIdGroup(toId(candidate.sectorId), filters.sectorIds)
  && matchesIdGroup(toId(candidate.industryId), filters.industryIds);

export const resolveStockSectorId = (stock: Stock, industryToSectorMap: ReadonlyMap<number, number>): number | null => {
  const fromIndustry = toId(stock.industry?.sector?.id) ?? toId(stock.industryId != null ? industryToSectorMap.get(stock.industryId) : null);
  if (fromIndustry != null) {
    return fromIndustry;
  }

  return toId(stock.sector?.id);
};

export const resolveStockIndustryId = (stock: Stock): number | null =>
  toId(stock.industryId) ?? toId(stock.industry?.id);

export const resolveConstituentSectorId = (
  constituent: IndexConstituentDto,
  industryToSectorMap: ReadonlyMap<number, number>,
): number | null => {
  const fromDto = toId(constituent.sectorId);
  if (fromDto != null) {
    return fromDto;
  }

  return toId(constituent.industryId != null ? industryToSectorMap.get(constituent.industryId) : null);
};

export const resolveConstituentIndustryId = (constituent: IndexConstituentDto): number | null =>
  toId(constituent.industryId);

export const matchesStockAdvancedFilters = (
  stock: Stock,
  filters: AdvancedStockFilters,
  industryToSectorMap: ReadonlyMap<number, number>,
): boolean => matchesAdvancedStockFilters(
  {
    exchange: stock.exchange,
    sectorId: resolveStockSectorId(stock, industryToSectorMap),
    industryId: resolveStockIndustryId(stock),
  },
  filters,
);

export const matchesConstituentAdvancedFilters = (
  constituent: IndexConstituentDto,
  filters: AdvancedStockFilters,
  industryToSectorMap: ReadonlyMap<number, number>,
): boolean => matchesAdvancedStockFilters(
  {
    exchange: constituent.exchange,
    sectorId: resolveConstituentSectorId(constituent, industryToSectorMap),
    industryId: resolveConstituentIndustryId(constituent),
  },
  filters,
);
