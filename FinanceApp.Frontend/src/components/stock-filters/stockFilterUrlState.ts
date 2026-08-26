import type { SectorDto } from '../../types';
import {
  EXCHANGE_FILTER_ORDER,
  normalizeAdvancedStockFilters,
  type AdvancedExchangeFilter,
  type AdvancedStockFilters,
} from './stockFilterModel';

export const ADVANCED_FILTER_EXCHANGES_PARAM = 'exchanges';
export const ADVANCED_FILTER_SECTORS_PARAM = 'sectors';
export const ADVANCED_FILTER_INDUSTRIES_PARAM = 'industries';

type AdvancedStockFilterParamNames = {
  exchangesParam?: string;
  sectorsParam?: string;
  industriesParam?: string;
};

const resolveAdvancedStockFilterParamNames = (names?: AdvancedStockFilterParamNames) => ({
  exchangesParam: names?.exchangesParam ?? ADVANCED_FILTER_EXCHANGES_PARAM,
  sectorsParam: names?.sectorsParam ?? ADVANCED_FILTER_SECTORS_PARAM,
  industriesParam: names?.industriesParam ?? ADVANCED_FILTER_INDUSTRIES_PARAM,
});

const parseCsvParams = (params: URLSearchParams, name: string): string[] =>
  params
    .getAll(name)
    .flatMap((value) => value.split(','))
    .map((value) => value.trim())
    .filter((value) => value.length > 0);

const parseExchangeParams = (params: URLSearchParams, paramName: string): AdvancedExchangeFilter[] => {
  const values = parseCsvParams(params, paramName)
    .map((value) => value.toLowerCase())
    .filter((value): value is AdvancedExchangeFilter => value === 'fra' || value === 'us');

  return EXCHANGE_FILTER_ORDER.filter((value) => values.includes(value));
};

const parseNumberParams = (params: URLSearchParams, name: string): number[] =>
  parseCsvParams(params, name)
    .map((value) => Number(value))
    .filter((value) => Number.isInteger(value) && value > 0);

const buildAvailableSets = (sectors: readonly SectorDto[]) => {
  const sectorIds = new Set<number>();
  const industryIds = new Set<number>();
  const industryToSectorId = new Map<number, number>();

  for (const sector of sectors) {
    if (sector.isArchived) {
      continue;
    }

    sectorIds.add(sector.id);
    for (const industry of sector.industries) {
      if (industry.isArchived) {
        continue;
      }

      industryIds.add(industry.id);
      industryToSectorId.set(industry.id, sector.id);
    }
  }

  return { sectorIds, industryIds, industryToSectorId };
};

export const pruneInvalidIndustrySelections = (
  industryIds: readonly number[],
  sectorIds: readonly number[],
  sectors: readonly SectorDto[],
): number[] => {
  if (sectors.length === 0) {
    return [...industryIds];
  }

  const { industryIds: availableIndustryIds, industryToSectorId } = buildAvailableSets(sectors);
  const sectorSet = new Set(sectorIds);

  return industryIds.filter((industryId) => {
    if (!availableIndustryIds.has(industryId)) {
      return false;
    }

    if (sectorSet.size === 0) {
      return true;
    }

    const sectorId = industryToSectorId.get(industryId);
    return sectorId != null && sectorSet.has(sectorId);
  });
};

export const normalizeAdvancedStockFiltersWithDirectory = (
  filters: Partial<AdvancedStockFilters> | null | undefined,
  sectors: readonly SectorDto[],
): AdvancedStockFilters => {
  const normalized = normalizeAdvancedStockFilters(filters);
  if (sectors.length === 0) {
    return normalized;
  }

  const { sectorIds: activeSectorIds, industryIds: activeIndustryIds } = buildAvailableSets(sectors);

  const nextSectorIds = normalized.sectorIds.filter((sectorId) => activeSectorIds.has(sectorId));
  const nextIndustryIds = pruneInvalidIndustrySelections(
    normalized.industryIds.filter((industryId) => activeIndustryIds.has(industryId)),
    nextSectorIds,
    sectors,
  );

  return {
    exchanges: normalized.exchanges,
    sectorIds: nextSectorIds,
    industryIds: nextIndustryIds,
  };
};

export const parseAdvancedStockFiltersFromSearchParams = (
  searchParams: URLSearchParams,
  paramNames?: AdvancedStockFilterParamNames,
): AdvancedStockFilters => {
  const { exchangesParam, sectorsParam, industriesParam } = resolveAdvancedStockFilterParamNames(paramNames);
  return normalizeAdvancedStockFilters({
    exchanges: parseExchangeParams(searchParams, exchangesParam),
    sectorIds: parseNumberParams(searchParams, sectorsParam),
    industryIds: parseNumberParams(searchParams, industriesParam),
  });
};

const encodeNumberValues = (values: readonly number[]): string => values.join(',');

const encodeExchangeValues = (values: readonly AdvancedExchangeFilter[]): string => values.join(',');

export const serializeAdvancedStockFiltersToSearchParams = (
  current: URLSearchParams,
  filters: AdvancedStockFilters,
  paramNames?: AdvancedStockFilterParamNames,
): URLSearchParams => {
  const { exchangesParam, sectorsParam, industriesParam } = resolveAdvancedStockFilterParamNames(paramNames);
  const next = new URLSearchParams(current);

  next.delete(exchangesParam);
  next.delete(sectorsParam);
  next.delete(industriesParam);

  if (filters.exchanges.length > 0) {
    next.set(exchangesParam, encodeExchangeValues(filters.exchanges));
  }

  if (filters.sectorIds.length > 0) {
    next.set(sectorsParam, encodeNumberValues(filters.sectorIds));
  }

  if (filters.industryIds.length > 0) {
    next.set(industriesParam, encodeNumberValues(filters.industryIds));
  }

  return next;
};

export const getIndustryOptionsForSelectedSectors = (
  sectors: readonly SectorDto[],
  selectedSectorIds: readonly number[],
): Array<{ value: number; label: string }> => {
  const selectedSet = new Set(selectedSectorIds);
  const selected = selectedSet.size > 0;

  return sectors
    .filter((sector) => !sector.isArchived)
    .flatMap((sector) => {
      if (selected && !selectedSet.has(sector.id)) {
        return [];
      }

      return sector.industries
        .filter((industry) => !industry.isArchived)
        .map((industry) => ({ value: industry.id, label: industry.name }));
    })
    .sort((a, b) => a.label.localeCompare(b.label, 'ru-RU', { sensitivity: 'base' }));
};

export const getActiveSectorOptions = (sectors: readonly SectorDto[]): Array<{ value: number; label: string }> =>
  sectors
    .filter((sector) => !sector.isArchived)
    .map((sector) => ({ value: sector.id, label: sector.name }))
    .sort((a, b) => a.label.localeCompare(b.label, 'ru-RU', { sensitivity: 'base' }));
