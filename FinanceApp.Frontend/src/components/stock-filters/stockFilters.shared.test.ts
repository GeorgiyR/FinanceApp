import {
  describe,
  expect,
  it,
} from 'vitest';
import type { IndexConstituentDto, SectorDto, Stock } from '../../types';
import {
  buildIndustryToSectorMap,
  matchesAdvancedStockFilters,
  matchesConstituentAdvancedFilters,
  matchesStockAdvancedFilters,
  normalizeExchangeFilterGroup,
} from './stockFilterEngine';
import {
  countActiveAdvancedFilterGroups,
  normalizeAdvancedStockFilters,
} from './stockFilterModel';
import {
  normalizeAdvancedStockFiltersWithDirectory,
  parseAdvancedStockFiltersFromSearchParams,
  pruneInvalidIndustrySelections,
  serializeAdvancedStockFiltersToSearchParams,
} from './stockFilterUrlState';

const sectors: SectorDto[] = [
  {
    id: 1,
    name: 'Energy',
    normalizedName: 'ENERGY',
    isArchived: false,
    sortOrder: 1,
    createdAtUtc: '',
    updatedAtUtc: '',
    industryCount: 1,
    stockCount: 1,
    industries: [{
      id: 10,
      sectorId: 1,
      name: 'Oil & Gas',
      normalizedName: 'OIL & GAS',
      isArchived: false,
      sortOrder: 1,
      createdAtUtc: '',
      updatedAtUtc: '',
      stockCount: 1,
    }],
  },
  {
    id: 2,
    name: 'Materials',
    normalizedName: 'MATERIALS',
    isArchived: false,
    sortOrder: 2,
    createdAtUtc: '',
    updatedAtUtc: '',
    industryCount: 2,
    stockCount: 2,
    industries: [{
      id: 20,
      sectorId: 2,
      name: 'Gold',
      normalizedName: 'GOLD',
      isArchived: false,
      sortOrder: 1,
      createdAtUtc: '',
      updatedAtUtc: '',
      stockCount: 1,
    }, {
      id: 21,
      sectorId: 2,
      name: 'Archived Industry',
      normalizedName: 'ARCHIVED INDUSTRY',
      isArchived: true,
      sortOrder: 2,
      createdAtUtc: '',
      updatedAtUtc: '',
      stockCount: 0,
    }],
  },
];

const industryToSectorMap = buildIndustryToSectorMap(sectors);

describe('shared stock advanced filters', () => {
  it('normalizes exchange groups and combines OR-within/AND-across groups', () => {
    expect(normalizeExchangeFilterGroup('Frankfurt')).toBe('fra');
    expect(normalizeExchangeFilterGroup('FRA')).toBe('fra');
    expect(normalizeExchangeFilterGroup('nyse')).toBe('us');
    expect(normalizeExchangeFilterGroup('NASDAQ')).toBe('us');
    expect(normalizeExchangeFilterGroup('LSE')).toBeNull();

    const filters = normalizeAdvancedStockFilters({ exchanges: ['fra', 'us'], sectorIds: [2], industryIds: [20] });
    expect(matchesAdvancedStockFilters({ exchange: 'NYSE', sectorId: 2, industryId: 20 }, filters)).toBe(true);
    expect(matchesAdvancedStockFilters({ exchange: 'NASDAQ', sectorId: 2, industryId: 20 }, filters)).toBe(true);
    expect(matchesAdvancedStockFilters({ exchange: 'Frankfurt', sectorId: 2, industryId: 20 }, filters)).toBe(true);
    expect(matchesAdvancedStockFilters({ exchange: 'Frankfurt', sectorId: 1, industryId: 10 }, filters)).toBe(false);
    expect(matchesAdvancedStockFilters({ exchange: 'NYSE', sectorId: 2, industryId: null }, filters)).toBe(false);
  });

  it('filters stocks and constituents by stable IDs', () => {
    const filters = normalizeAdvancedStockFilters({ exchanges: ['us'], sectorIds: [2], industryIds: [20] });
    const stock: Stock = {
      id: 1,
      ticker: 'NEM',
      name: 'Newmont',
      commonName: 'Newmont',
      exchange: 'NYSE',
      currentPrice: 1,
      updatedAt: '',
      industryId: 20,
      sector: { id: 2, name: 'Materials', isArchived: false },
    };
    const constituent: IndexConstituentDto = {
      stockId: 1,
      ticker: 'NEM',
      name: 'Newmont',
      exchange: 'NASDAQ',
      trackingStatus: 'CatalogOnly',
      importedAt: '',
      sectorId: 2,
      industryId: 20,
    };

    expect(matchesStockAdvancedFilters(stock, filters, industryToSectorMap)).toBe(true);
    expect(matchesConstituentAdvancedFilters(constituent, filters, industryToSectorMap)).toBe(true);
    expect(matchesConstituentAdvancedFilters({ ...constituent, industryId: null }, filters, industryToSectorMap)).toBe(false);
  });

  it('counts active groups and prunes invalid draft industries on sector change', () => {
    expect(countActiveAdvancedFilterGroups(normalizeAdvancedStockFilters({}))).toBe(0);
    expect(countActiveAdvancedFilterGroups(normalizeAdvancedStockFilters({ exchanges: ['fra', 'us'] }))).toBe(1);
    expect(countActiveAdvancedFilterGroups(normalizeAdvancedStockFilters({ exchanges: ['us'], industryIds: [20, 10] }))).toBe(2);

    expect(pruneInvalidIndustrySelections([10, 20, 21], [1], sectors)).toEqual([10]);
    expect(pruneInvalidIndustrySelections([10, 20], [], sectors)).toEqual([10, 20]);
  });

  it('parses/serializes URL params safely and preserves unrelated params', () => {
    const params = new URLSearchParams('tab=constituents&exchanges=us,fra,unknown,,us&sectors=2,2,bad,999&industries=20,10,21,xxx');
    const parsed = parseAdvancedStockFiltersFromSearchParams(params);
    expect(parsed).toEqual({ exchanges: ['fra', 'us'], sectorIds: [2, 999], industryIds: [10, 20, 21] });

    const normalized = normalizeAdvancedStockFiltersWithDirectory(parsed, sectors);
    expect(normalized).toEqual({ exchanges: ['fra', 'us'], sectorIds: [2], industryIds: [20] });

    const serialized = serializeAdvancedStockFiltersToSearchParams(params, normalized);
    expect(serialized.get('tab')).toBe('constituents');
    expect(serialized.get('exchanges')).toBe('fra,us');
    expect(serialized.get('sectors')).toBe('2');
    expect(serialized.get('industries')).toBe('20');
  });

  it('supports mode-specific advanced-filter URL parameter names', () => {
    const params = new URLSearchParams('texchanges=us,fra&tsectors=2&tindustries=20');
    const parsed = parseAdvancedStockFiltersFromSearchParams(params, {
      exchangesParam: 'texchanges',
      sectorsParam: 'tsectors',
      industriesParam: 'tindustries',
    });
    expect(parsed).toEqual({ exchanges: ['fra', 'us'], sectorIds: [2], industryIds: [20] });

    const serialized = serializeAdvancedStockFiltersToSearchParams(
      new URLSearchParams('foo=bar'),
      parsed,
      { exchangesParam: 'texchanges', sectorsParam: 'tsectors', industriesParam: 'tindustries' },
    );
    expect(serialized.get('foo')).toBe('bar');
    expect(serialized.get('texchanges')).toBe('fra,us');
    expect(serialized.get('tsectors')).toBe('2');
    expect(serialized.get('tindustries')).toBe('20');
  });
});
