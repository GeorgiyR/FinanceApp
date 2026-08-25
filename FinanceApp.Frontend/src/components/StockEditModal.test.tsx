import { describe, expect, it } from 'vitest';
import { readFileSync } from 'fs';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import {
  buildCreateStockPayload,
  buildIndustryOptions,
  buildMarketIndexOptions,
  buildStockFormValues,
  buildUpdateStockMetadataPayload,
  validateStockFinanzenNetSlug,
  validateStockIsin,
  validateStockWkn,
} from './StockEditModal';
import type { MarketIndex, SectorDto, Stock } from '../types';

const __dirname = dirname(fileURLToPath(import.meta.url));
const modalSource = readFileSync(join(__dirname, 'StockEditModal.tsx'), 'utf8');

describe('StockEditModal helpers', () => {
  it('builds edit form values from authoritative stock metadata', () => {
    const stock: Stock = {
      id: 7,
      ticker: 'AAPL',
      name: 'Apple Inc.',
      commonName: 'Apple',
      exchange: 'NASDAQ',
      currentPrice: 201.5,
      updatedAt: '2026-08-18T00:00:00Z',
      wkn: '865985',
      isin: 'US0378331005',
      finanzenNetSlug: 'apple-aktie',
      industryId: 11,
      sector: { id: 3, name: 'Technology', isArchived: false },
      marketIndexIds: [1, 4],
    };

    expect(buildStockFormValues(stock)).toEqual({
      ticker: 'AAPL',
      name: 'Apple Inc.',
      commonName: 'Apple',
      exchange: 'NASDAQ',
      currentPrice: 201.5,
      wkn: '865985',
      isin: 'US0378331005',
      finanzenNetSlug: 'apple-aktie',
      sectorId: 3,
      industryId: 11,
      marketIndexIds: [1, 4],
    });
  });

  it('keeps currently assigned archived market indices selectable while excluding unrelated archived ones', () => {
    const indices: MarketIndex[] = [
      { id: 1, code: 'SPX', name: 'S&P 500', description: '', countryOrRegion: '', sortOrder: 1, isArchived: false, showInNavigation: true },
      { id: 2, code: 'OLD', name: 'Archived Current', description: '', countryOrRegion: '', sortOrder: 2, isArchived: true, showInNavigation: true },
      { id: 3, code: 'HID', name: 'Archived Other', description: '', countryOrRegion: '', sortOrder: 3, isArchived: true, showInNavigation: true },
    ];

    const options = buildMarketIndexOptions({
      marketIndices: indices,
      selectedMarketIndexIds: [1, 2],
    });

    expect(options.map((option) => option.value)).toEqual([1, 2]);
  });

  it('keeps archived industry selectable only for the stocks current sector binding', () => {
    const sectors: SectorDto[] = [
      {
        id: 10,
        name: 'Technology',
        normalizedName: 'technology',
        isArchived: false,
        sortOrder: 1,
        createdAtUtc: '',
        updatedAtUtc: '',
        industryCount: 1,
        stockCount: 1,
        industries: [{
          id: 11,
          sectorId: 10,
          name: 'Software',
          normalizedName: 'software',
          isArchived: true,
          sortOrder: 1,
          createdAtUtc: '',
          updatedAtUtc: '',
          stockCount: 1,
        }],
      },
    ];
    const stock: Stock = {
      id: 5,
      ticker: 'SAP',
      name: 'SAP SE',
      commonName: 'SAP',
      exchange: 'Frankfurt',
      currentPrice: 100,
      updatedAt: '',
      industryId: 11,
      sector: { id: 10, name: 'Technology', isArchived: false },
      industry: { id: 11, name: 'Software', isArchived: true },
    };

    const options = buildIndustryOptions({
      sectors,
      stock,
      selectedSectorId: 10,
    });

    expect(options).toHaveLength(1);
    expect(options[0]?.value).toBe(11);
  });

  it('matches the Stocks page validation rules for WKN, ISIN, and finanzen.net slug', () => {
    expect(validateStockWkn('865985')).toBe(true);
    expect(validateStockWkn('')).toBe(true);
    expect(validateStockWkn('12')).toContain('WKN');

    expect(validateStockIsin('US0378331005')).toBe(true);
    expect(validateStockIsin('')).toBe(true);
    expect(validateStockIsin('US123')).toContain('ISIN');

    expect(validateStockFinanzenNetSlug('apple-aktie')).toBe(true);
    expect(validateStockFinanzenNetSlug('')).toBe(true);
    expect(validateStockFinanzenNetSlug('Bad/Slug')).toContain('строчные буквы');
  });

  it('includes selected sectorId in create/update payloads and serializes clearing as null', () => {
    const createPayload = buildCreateStockPayload({
      ticker: 'SAP',
      name: 'SAP SE',
      exchange: 'Frankfurt',
      currentPrice: 100,
      sectorId: 10,
      industryId: undefined,
    });
    expect(createPayload.sectorId).toBe(10);
    expect(createPayload.industryId).toBeNull();

    const updatePayload = buildUpdateStockMetadataPayload({
      ticker: 'SAP',
      name: 'SAP SE',
      exchange: 'Frankfurt',
      currentPrice: 100,
      sectorId: undefined,
      industryId: undefined,
    });
    expect(updatePayload.sectorId).toBeNull();
    expect(updatePayload.industryId).toBeNull();
  });
});

describe('StockEditModal source contracts', () => {
  it('keeps ticker and exchange immutable in edit mode by default with explicit unlock action', () => {
    expect(modalSource).toContain("extra={mode === 'edit' ? IDENTITY_IMMUTABLE_HELPER : undefined}");
    expect(modalSource).toContain("disabled={mode === 'edit' && (!allowIdentityEditing || !identityEditingEnabled)}");
    expect(modalSource).toContain('Изменить тикер / биржу');
  });

  it('shows loading state and submission lock inside the reusable modal', () => {
    expect(modalSource).toContain('<Spin />');
    expect(modalSource).toContain('loading={submitting}');
  });

  it('supports inline modal errors and explicit read-only identity mode for shared usages', () => {
    expect(modalSource).toContain('inlineError');
    expect(modalSource).toContain('Изменение тикера/биржи недоступно в этом режиме');
  });
});
