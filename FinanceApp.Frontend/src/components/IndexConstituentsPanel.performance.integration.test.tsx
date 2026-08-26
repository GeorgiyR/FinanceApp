// @vitest-environment jsdom
import React from 'react';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import IndexConstituentsPanel from './IndexConstituentsPanel';
import { loadStockMetadataLookups } from './StockEditModal';

vi.mock('../services/api', () => ({
  getIndexConstituentHistory: vi.fn(),
  getIndexConstituents: vi.fn(),
  getIndexConstituentHistoryRefreshJob: vi.fn(),
  getIndexConstituentPerformance: vi.fn(),
  getStock: vi.fn(),
  getStockPrice: vi.fn(),
  refreshIndexConstituentHistory: vi.fn(),
  refreshIndexConstituents: vi.fn(),
  refreshIndexConstituentsHistory: vi.fn(),
  startIndexConstituentsBatchQuoteRefresh: vi.fn(),
  getIndexConstituentsBatchQuoteRefreshJob: vi.fn(),
  trackStock: vi.fn(),
  updateStockMetadata: vi.fn(),
  updateStockQuote: vi.fn(),
}));

vi.mock('./StockEditModal', () => ({
  default: () => null,
  buildUpdateStockMetadataPayload: vi.fn(() => ({})),
  loadStockMetadataLookups: vi.fn().mockResolvedValue({
    sectors: [],
    marketIndices: [],
    marketIndicesLoadFailed: false,
  }),
}));

vi.mock('./StockFundamentalsDrawer', () => ({ default: () => null }));
vi.mock('./StockPriceChart', () => ({ default: () => null }));

const baseConstituentsResponse = {
  data: {
    marketIndexId: 1,
    indexName: 'DAX',
    totalCount: 3,
    constituents: [
      { stockId: 1, ticker: 'DAXA', name: 'Align Technology', commonName: 'Align Technology, Inc.', isin: 'US0162551016', sector: 'Information Technology', industry: 'Software', exchange: 'Frankfurt', trackingStatus: 'CatalogOnly', importedAt: '2026-08-19T00:00:00Z' },
      { stockId: 2, ticker: 'OTHR', name: 'Microsoft', commonName: 'Microsoft', isin: 'US5949181045', sector: 'Financials', industry: null, exchange: 'NYSE', trackingStatus: 'CatalogOnly', importedAt: '2026-08-19T00:00:00Z' },
      { stockId: 3, ticker: 'NONE', name: 'No Index', commonName: 'No Index', exchange: 'NYSE', trackingStatus: 'CatalogOnly', importedAt: '2026-08-19T00:00:00Z' },
    ],
  },
};

const buildPerformanceResponse = (range: string, items: Array<{ stockId: number; changePercent: number | null }>) => ({
  data: {
    marketIndexId: 1,
    range,
    generatedAtUtc: '2026-08-19T00:00:00Z',
    items: items.map((item) => ({
      stockId: item.stockId,
      startPrice: item.changePercent == null ? null : 100,
      endPrice: item.changePercent == null ? null : 100 * (1 + item.changePercent / 100),
      changePercent: item.changePercent,
      startAtUtc: '2026-08-18T00:00:00Z',
      endAtUtc: '2026-08-19T00:00:00Z',
      dataStatus: item.changePercent == null ? 'InsufficientData' : 'Available',
    })),
  },
});

describe('IndexConstituentsPanel 24h performance integration', () => {
  beforeEach(async () => {
    vi.clearAllMocks();
    window.history.replaceState({}, '', '/market-indices/1?tab=constituents');
    const api = await import('../services/api');
    vi.mocked(api.getIndexConstituents).mockResolvedValue(baseConstituentsResponse);
    vi.mocked(api.getIndexConstituentPerformance).mockResolvedValue(buildPerformanceResponse('24h', [
      { stockId: 3, changePercent: 7 },
      { stockId: 1, changePercent: 2 },
      { stockId: 2, changePercent: null },
    ]));
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('requests 24h performance and sorts globally with unavailable values last', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');

    render(<IndexConstituentsPanel indexId={1} isArchived={false} />);
    await waitFor(() => expect(screen.getAllByText('DAXA').length).toBeGreaterThan(0));

    const sortSelect = screen.getAllByRole('combobox', { name: 'Сортировка' })[0]!;
    await user.click(sortSelect);
    await user.click(await screen.findByText('24 ч.'));

    await waitFor(() => expect(vi.mocked(api.getIndexConstituentPerformance)).toHaveBeenCalledWith(1, '24h', expect.any(AbortSignal)));
    await waitFor(() => expect(screen.getByText('+7,00 %')).toBeInTheDocument());

    const rows = Array.from(document.querySelectorAll('tr[data-row-key]'));
    const ids = rows.map((row) => row.getAttribute('data-row-key'));
    expect(ids.indexOf('3')).toBeLessThan(ids.indexOf('1'));
    expect(ids.indexOf('1')).toBeLessThan(ids.indexOf('2'));
  });

  it('renders compact right-aligned classification codes in constituent table', async () => {
    render(<IndexConstituentsPanel indexId={1} isArchived={false} />);
    await waitFor(() => expect(screen.getAllByText('DAXA').length).toBeGreaterThan(0));

    const daxRow = document.querySelector('tr[data-row-key="1"]');
    const otherRow = document.querySelector('tr[data-row-key="2"]');
    const noneRow = document.querySelector('tr[data-row-key="3"]');
    expect(daxRow).not.toBeNull();
    expect(otherRow).not.toBeNull();
    expect(noneRow).not.toBeNull();

    expect(screen.getByText('IT')).toBeInTheDocument();
    expect(screen.getByText('FIN')).toBeInTheDocument();
    expect(screen.queryByText('Information Technology')).not.toBeInTheDocument();
    expect(screen.queryByText('Financials')).not.toBeInTheDocument();
    expect(screen.queryByText('СЕК')).not.toBeInTheDocument();
    expect(screen.queryByText('ОТР')).not.toBeInTheDocument();

    const classification = (daxRow as HTMLElement).querySelector('[aria-label="Классификация: Information Technology · Software"]');
    expect(classification).not.toBeNull();
    expect(classification).toHaveAttribute('title', 'Information Technology · Software');
    expect(classification).toHaveStyle({ marginLeft: 'auto', textAlign: 'right', fontSize: '14px' });
    expect(classification?.closest('.ant-tag')).toBeNull();

    const daxNameCell = (daxRow as HTMLElement).querySelectorAll('td')[1] as HTMLElement;
    const nameCellWrapper = daxNameCell.firstElementChild as HTMLElement;
    const topRow = nameCellWrapper.firstElementChild as HTMLElement;
    const companyName = topRow.firstElementChild as HTMLElement;

    expect(nameCellWrapper).toHaveStyle({ display: 'flex', flexDirection: 'column', width: '100%', minWidth: '0' });
    expect(topRow).toHaveStyle({ display: 'flex', alignItems: 'center', gap: '8px', width: '100%', minWidth: '0' });
    expect(companyName).toHaveStyle({ minWidth: '0', flex: '1 1 auto', fontSize: '16px' });
    expect(companyName).toHaveTextContent('Align Technology');
    expect(nameCellWrapper).toHaveTextContent('Align Technology, Inc.');
    expect(nameCellWrapper).toHaveTextContent('US0162551016');

    expect((noneRow as HTMLElement).querySelector('[aria-label^="Классификация:"]')).toBeNull();
  });

  it('does not let stale older range responses overwrite the latest selection', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');

    let resolve1y: ((value: unknown) => void) | null = null;
    vi.mocked(api.getIndexConstituentPerformance).mockImplementation((_, range) => {
      if (range === '1y') {
        return new Promise((resolve) => { resolve1y = resolve; });
      }
      return Promise.resolve(buildPerformanceResponse('24h', [
        { stockId: 3, changePercent: 7 },
        { stockId: 1, changePercent: 2 },
        { stockId: 2, changePercent: null },
      ]));
    });

    render(<IndexConstituentsPanel indexId={1} isArchived={false} />);
    await waitFor(() => expect(screen.getAllByText('DAXA').length).toBeGreaterThan(0));

    const sortSelect = screen.getAllByRole('combobox', { name: 'Сортировка' })[0]!;
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));
    await user.click(sortSelect);
    await user.click(await screen.findByText('24 ч.'));
    await waitFor(() => expect(screen.getByText('+7,00 %')).toBeInTheDocument());

    act(() => {
      resolve1y?.(buildPerformanceResponse('1y', [
        { stockId: 1, changePercent: 40 },
        { stockId: 2, changePercent: 30 },
        { stockId: 3, changePercent: 20 },
      ]));
    });

    await waitFor(() => expect(screen.getByText('+7,00 %')).toBeInTheDocument());
    expect(screen.queryByText('+40,00 %')).not.toBeInTheDocument();
  });

  it('shows a visible error state when the 24h performance request fails', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getIndexConstituentPerformance).mockRejectedValue(new Error('network'));

    render(<IndexConstituentsPanel indexId={1} isArchived={false} />);
    await waitFor(() => expect(screen.getAllByText('DAXA').length).toBeGreaterThan(0));

    const sortSelect = screen.getAllByRole('combobox', { name: 'Сортировка' })[0]!;
    await user.click(sortSelect);
    await user.click(await screen.findByText('24 ч.'));

    await waitFor(() => expect(screen.getByText('Ошибка загрузки данных о росте')).toBeInTheDocument());
  });

  it('applies US advanced exchange filter for both NYSE and NASDAQ, then resets via back navigation', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getIndexConstituents).mockResolvedValue({
      data: {
        ...baseConstituentsResponse.data,
        constituents: [
          { stockId: 1, ticker: 'FRA1', name: 'Frankfurt One', exchange: 'Frankfurt', trackingStatus: 'CatalogOnly', importedAt: '', sectorId: 1, industryId: 10 },
          { stockId: 2, ticker: 'NYSE1', name: 'Nyse One', exchange: 'NYSE', trackingStatus: 'CatalogOnly', importedAt: '', sectorId: 2, industryId: 20 },
          { stockId: 3, ticker: 'NAS1', name: 'Nasdaq One', exchange: 'NASDAQ', trackingStatus: 'CatalogOnly', importedAt: '', sectorId: 2, industryId: 20 },
        ],
      },
    });
    vi.mocked(loadStockMetadataLookups).mockResolvedValue({
      sectors: [
        {
          id: 2,
          name: 'Materials',
          normalizedName: 'MATERIALS',
          isArchived: false,
          sortOrder: 1,
          createdAtUtc: '',
          updatedAtUtc: '',
          industryCount: 1,
          stockCount: 1,
          industries: [{ id: 20, sectorId: 2, name: 'Gold', normalizedName: 'GOLD', isArchived: false, sortOrder: 1, createdAtUtc: '', updatedAtUtc: '', stockCount: 1 }],
        },
      ],
      marketIndices: [],
      marketIndicesLoadFailed: false,
    });

    render(<IndexConstituentsPanel indexId={1} isArchived={false} />);
    await waitFor(() => expect(screen.getAllByText('FRA1').length).toBeGreaterThan(0));

    await user.click(screen.getByRole('button', { name: 'Открыть расширенные фильтры' }));
    await user.click(screen.getByRole('combobox', { name: 'Фильтр по бирже' }));
    await user.click(await screen.findByText('США (NYSE + NASDAQ)'));
    await user.click(screen.getByRole('button', { name: 'Применить фильтры' }));

    await waitFor(() => expect(screen.queryByText('FRA1')).not.toBeInTheDocument());
    expect(screen.getAllByText('NYSE1').length).toBeGreaterThan(0);
    expect(screen.getAllByText('NAS1').length).toBeGreaterThan(0);
    expect(window.location.search).toContain('exchanges=us');

    window.history.back();
    window.dispatchEvent(new PopStateEvent('popstate'));

    await waitFor(() => expect(screen.getAllByText('FRA1').length).toBeGreaterThan(0));
  });

  it('canonicalizes invalid advanced filter URL params while preserving unrelated query state', async () => {
    window.history.replaceState({}, '', '/market-indices/1?tab=constituents&exchanges=us,fra,us,unknown&sectors=2,999&industries=20,21,1000');
    vi.mocked(loadStockMetadataLookups).mockResolvedValue({
      sectors: [
        {
          id: 2,
          name: 'Materials',
          normalizedName: 'MATERIALS',
          isArchived: false,
          sortOrder: 1,
          createdAtUtc: '',
          updatedAtUtc: '',
          industryCount: 1,
          stockCount: 1,
          industries: [{ id: 20, sectorId: 2, name: 'Gold', normalizedName: 'GOLD', isArchived: false, sortOrder: 1, createdAtUtc: '', updatedAtUtc: '', stockCount: 1 }],
        },
      ],
      marketIndices: [],
      marketIndicesLoadFailed: false,
    });

    render(<IndexConstituentsPanel indexId={1} isArchived={false} />);
    await waitFor(() => expect(screen.getByText('Фильтры (3)')).toBeInTheDocument());

    expect(window.location.search).toContain('tab=constituents');
    expect(window.location.search).toContain('exchanges=fra%2Cus');
    expect(window.location.search).toContain('sectors=2');
    expect(window.location.search).toContain('industries=20');
  });
});
