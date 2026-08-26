// @vitest-environment jsdom
import React from 'react';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import AuthContext from '../contexts/AuthContext';
import type { Stock } from '../types';
import StocksPage, {
  CATALOG_SORT_MODE_OPTIONS,
  CATALOG_SORT_NAME_MODE,
  isCatalogPeriodSortMode,
} from './StocksPage';
import { STOCK_HISTORY_RANGE_OPTIONS } from '../components/historyRangeOptions';

const trackedStock: Stock = {
  id: 1,
  ticker: 'AAPL',
  name: 'Apple Inc.',
  commonName: 'Apple',
  exchange: 'NYSE',
  currentPrice: 100,
  updatedAt: '2026-08-18T00:00:00Z',
  trackingStatus: 1,
  marketIndexIds: [1],
  sector: { id: 15, name: 'Information Technology', isArchived: false },
  industry: {
    id: 1501,
    name: 'Software',
    isArchived: false,
    sector: { id: 15, name: 'Information Technology', isArchived: false },
  },
};

const untrackedStock: Stock = {
  id: 2,
  ticker: 'BAS',
  name: 'BASF SE',
  commonName: 'BASF',
  exchange: 'Frankfurt',
  currentPrice: 50,
  updatedAt: '2026-08-18T00:00:00Z',
  trackingStatus: 0,
  marketIndexIds: [],
  sector: { id: 20, name: 'Materials', isArchived: false },
};

const mcdCatalogStock: Stock = {
  id: 4,
  ticker: 'MCD',
  name: "McDonald's Corporation",
  commonName: "McDonald's",
  exchange: 'NYSE',
  currentPrice: 300,
  updatedAt: '2026-08-18T00:00:00Z',
  trackingStatus: 0,
  marketIndexIds: [],
  industry: {
    id: 1901,
    name: 'Restaurants',
    isArchived: false,
    sector: null,
  },
};

const buildHistoryResponse = (range: '1y' | '6m') => ({
  range,
  interval: '1d',
  currency: 'USD',
  financialCurrency: 'USD',
  normalizedQuoteCurrency: 'USD',
  quoteUnitMultiplier: 1,
  rateToEur: null,
  rateTimestampUtc: null,
  rateSource: null,
  conversionWarning: null,
  volumeMetrics: {
    averageVolume20: 1000,
    averageVolume50: 900,
    relativeVolume: 1.2,
    turnover: 305000,
    turnoverCurrency: 'USD',
    latestMetricsTimestamp: '2026-08-02T00:00:00Z',
    usesCompletedCandle: true,
  },
  points: range === '6m'
    ? [
        {
          timestamp: '2026-07-01T00:00:00Z',
          interval: '1d',
          openRaw: 320,
          highRaw: 325,
          lowRaw: 318,
          closeRaw: 322,
          openNormalized: 320,
          highNormalized: 325,
          lowNormalized: 318,
          closeNormalized: 322,
          openEur: null,
          highEur: null,
          lowEur: null,
          closeEur: null,
          volume: 1200,
        },
        {
          timestamp: '2026-08-02T00:00:00Z',
          interval: '1d',
          openRaw: 326,
          highRaw: 331,
          lowRaw: 324,
          closeRaw: 330,
          openNormalized: 326,
          highNormalized: 331,
          lowNormalized: 324,
          closeNormalized: 330,
          openEur: null,
          highEur: null,
          lowEur: null,
          closeEur: null,
          volume: 1400,
        },
      ]
    : [
        {
          timestamp: '2026-01-01T00:00:00Z',
          interval: '1d',
          openRaw: 290,
          highRaw: 295,
          lowRaw: 288,
          closeRaw: 292,
          openNormalized: 290,
          highNormalized: 295,
          lowNormalized: 288,
          closeNormalized: 292,
          openEur: null,
          highEur: null,
          lowEur: null,
          closeEur: null,
          volume: 1000,
        },
        {
          timestamp: '2026-08-02T00:00:00Z',
          interval: '1d',
          openRaw: 301,
          highRaw: 306,
          lowRaw: 300,
          closeRaw: 305,
          openNormalized: 301,
          highNormalized: 306,
          lowNormalized: 300,
          closeNormalized: 305,
          openEur: null,
          highEur: null,
          lowEur: null,
          closeEur: null,
          volume: 1300,
        },
      ],
});

const untrackedStockFRA: Stock = {
  id: 3,
  ticker: 'BMW',
  name: 'BMW AG',
  commonName: 'BMW',
  exchange: 'FRA',
  currentPrice: 80,
  updatedAt: '2026-08-18T00:00:00Z',
  trackingStatus: 0,
  marketIndexIds: [],
};

vi.mock('../services/api', async () => {
  const actual = await vi.importActual<typeof import('../services/api')>('../services/api');
  return {
    ...actual,
    getTrackedStocks: vi.fn(),
    getStockCatalog: vi.fn(),
    getPortfolios: vi.fn().mockResolvedValue({ data: [] }),
    getSectors: vi.fn().mockResolvedValue([]),
    getMarketIndices: vi.fn().mockResolvedValue([
      {
        id: 1,
        name: 'S&P 500',
        code: 'SPX',
        providerSymbol: null,
        description: '',
        countryOrRegion: '',
        sortOrder: 1,
        isArchived: false,
        showInNavigation: true,
      },
    ]),
    getStockPrice: vi.fn(),
    getStockHistory: vi.fn(),
    refreshStockHistory: vi.fn(),
    getIndexConstituentHistory: vi.fn(),
    getStockCatalogPerformance: vi.fn(),
    trackStock: vi.fn().mockResolvedValue({ data: {} }),
    untrackStock: vi.fn().mockResolvedValue({ data: {} }),
  };
});

const authValue = {
  token: 'token',
  user: { id: 1, username: 'test', email: 'test@example.com', roles: [] },
  login: () => {},
  logout: () => {},
  refreshUser: async () => {},
  isAuthenticated: true,
  loading: false,
};

const renderPage = (mode: 'tracked' | 'catalog') => render(
  <MemoryRouter initialEntries={[mode === 'catalog' ? '/stocks/catalog' : '/stocks']}>
    <AuthContext.Provider value={authValue}>
      <StocksPage mode={mode} />
    </AuthContext.Provider>
  </MemoryRouter>,
);

const expectRenderedBefore = (left: Element, right: Element) => {
  expect(left.compareDocumentPosition(right) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
};

const buildCatalogStock = (order: number): Stock => ({
  id: order + 100,
  ticker: `T${String(order).padStart(3, '0')}`,
  name: `Stock ${String(order).padStart(3, '0')}`,
  commonName: `Stock ${String(order).padStart(3, '0')}`,
  exchange: 'NYSE',
  currentPrice: 10,
  updatedAt: '2026-08-18T00:00:00Z',
  trackingStatus: 0,
  marketIndexIds: [],
});

const getRenderedRowIds = () =>
  Array.from(document.querySelectorAll('tr[data-row-key]')).map((row) => row.getAttribute('data-row-key'));

describe('StocksPage catalog mode', () => {
  beforeEach(async () => {
    window.history.replaceState({}, '', '/stocks/catalog');
    vi.clearAllMocks();
    const api = await import('../services/api');
    vi.mocked(api.getTrackedStocks).mockResolvedValue({ data: [trackedStock] });
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: [trackedStock, untrackedStock, untrackedStockFRA, mcdCatalogStock] });
    vi.mocked(api.getStockHistory).mockImplementation(async (_id, range) => ({ data: buildHistoryResponse(range as '1y' | '6m') }));
    vi.mocked(api.refreshStockHistory).mockResolvedValue({ data: { stockId: mcdCatalogStock.id, deletedPoints: 2, importedPoints: 2 } });
    vi.mocked(api.getIndexConstituentHistory).mockResolvedValue({ data: buildHistoryResponse('1y') });
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue({ data: { range: '1y', generatedAtUtc: '2026-08-19T00:00:00Z', items: [] } });
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  const getTickerToggleButton = (stockId: number, ticker: string) => {
    const row = document.querySelector(`tr[data-row-key="${stockId}"]`);
    expect(row).not.toBeNull();
    const button = within(row as HTMLElement).queryByRole('button', { name: `Открыть график цены: ${ticker}` })
      ?? within(row as HTMLElement).queryByRole('button', { name: `Закрыть график цены: ${ticker}` });
    expect(button).not.toBeNull();
    return button as HTMLButtonElement;
  };

  const getPaginationTrigger = (page: number) => {
    const trigger = document.querySelector(`li.ant-pagination-item-${page} a, li.ant-pagination-item-${page} button`);
    expect(trigger).not.toBeNull();
    return trigger as HTMLElement;
  };

  const expectActivePage = (page: number) => {
    expect(document.querySelector(`li.ant-pagination-item-${page}.ant-pagination-item-active`)).not.toBeNull();
  };

  const getHeaderActionsRow = () => {
    const row = screen.getByTestId('stocks-page-header-actions');
    expect(row.closest('.app-workspace-header-right')).not.toBeNull();
    return row;
  };

  const getToolbarRow = () => {
    const row = screen.getByTestId('stocks-page-toolbar-row');
    expect(row.closest('.app-workspace-content')).not.toBeNull();
    return row;
  };

  it('renders catalog layout as explicit header action row plus separate toolbar row with expected controls', async () => {
    const user = userEvent.setup();
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const headerActions = getHeaderActionsRow();
    const toolbar = getToolbarRow();
    const addButton = screen.getByRole('button', { name: /Добавить акцию/i });
    expect(headerActions).toContainElement(addButton);
    expect(toolbar).not.toContainElement(addButton);
    expectRenderedBefore(headerActions, toolbar);
    expect(toolbar).toHaveStyle({ flexWrap: 'wrap' });

    const searchInput = screen.getByPlaceholderText('Поиск: тикер, название, биржа, индекс');
    const openFiltersButton = screen.getByRole('button', { name: 'Открыть расширенные фильтры' });
    const resetFiltersButton = screen.getByRole('button', { name: 'Сбросить расширенные фильтры' });
    const sortLabel = screen.getByText('Сортировка:');
    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });

    expectRenderedBefore(searchInput, openFiltersButton);
    expectRenderedBefore(openFiltersButton, resetFiltersButton);
    expectRenderedBefore(resetFiltersButton, sortLabel);
    expectRenderedBefore(sortLabel, sortSelect);

    expect(screen.queryByRole('button', { name: 'Сортировать по возрастанию' })).not.toBeInTheDocument();

    await user.click(sortSelect);
    await user.click(await screen.findByText('24 ч.'));
    expect(screen.getByRole('button', { name: 'Сортировать по возрастанию' })).toBeInTheDocument();
    expect(screen.queryByText(/Авто-обновление через/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Обновить цены/i })).not.toBeInTheDocument();

    await user.click(addButton);
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
  });

  // Test 1: All exchange fixtures render in one table, not separate sections
  it('renders all stocks from different exchanges in a single unified table', async () => {
    renderPage('catalog');
    await waitFor(() => {
      expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0);
      expect(screen.getAllByText('BAS').length).toBeGreaterThan(0);
      expect(screen.getAllByText('BMW').length).toBeGreaterThan(0);
    });
    // No exchange-group headings (tracked mode uses h5 headings)
    expect(screen.queryByRole('heading', { name: 'Цены на франкфуртской бирже' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Цены на нью-йоркской бирже' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Портфель' })).not.toBeInTheDocument();
  });

  it('renders compact right-aligned classification codes with full tooltip/aria text', async () => {
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const appleRow = document.querySelector('tr[data-row-key="1"]');
    const basfRow = document.querySelector('tr[data-row-key="2"]');
    const mcdRow = document.querySelector('tr[data-row-key="4"]');
    const bmwRow = document.querySelector('tr[data-row-key="3"]');
    expect(appleRow).not.toBeNull();
    expect(basfRow).not.toBeNull();
    expect(mcdRow).not.toBeNull();
    expect(bmwRow).not.toBeNull();

    expect(within(appleRow as HTMLElement).getByText('IT')).toBeInTheDocument();
    expect(within(basfRow as HTMLElement).getByText('MAT')).toBeInTheDocument();
    expect(within(mcdRow as HTMLElement).getByText('REST')).toBeInTheDocument();
    expect(within(appleRow as HTMLElement).queryByText('Information Technology')).not.toBeInTheDocument();
    expect(within(basfRow as HTMLElement).queryByText('Materials')).not.toBeInTheDocument();
    expect(within(mcdRow as HTMLElement).queryByText('Restaurants')).not.toBeInTheDocument();
    expect(within(bmwRow as HTMLElement).queryByLabelText(/Классификация:/)).not.toBeInTheDocument();

    const appleClassification = within(appleRow as HTMLElement).getByLabelText('Классификация: Information Technology · Software');
    expect(appleClassification).toHaveAttribute('title', 'Information Technology · Software');
    expect(appleClassification).toHaveStyle({ marginLeft: 'auto', textAlign: 'right', fontSize: '14px' });

    expect(screen.queryByText('СЕК')).not.toBeInTheDocument();
    expect(screen.queryByText('ОТР')).not.toBeInTheDocument();
  });

  // Test 2: No visible «Статус», «Отслеживается» or «Не отслеживается» text
  it('does not show Статус column or status tags', async () => {
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect(screen.queryByText('Статус')).not.toBeInTheDocument();
    expect(screen.queryByText('Отслеживается')).not.toBeInTheDocument();
    expect(screen.queryByText('Не отслеживается')).not.toBeInTheDocument();
  });

  // Test 3: Untracked stock has an enabled icon-only add action
  it('untracked stock has enabled icon-only add-to-tracked button', async () => {
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('BAS').length).toBeGreaterThan(0));
    // There should be a button accessible by «Добавить в отслеживаемые»
    const addButtons = screen.getAllByRole('button', { name: 'Добавить в отслеживаемые' });
    expect(addButtons.length).toBeGreaterThan(0);
    // At least one should be enabled (untracked stock)
    const enabledAdd = addButtons.find((btn) => !(btn as HTMLButtonElement).disabled);
    expect(enabledAdd).toBeDefined();
  });

  // Test 4: Tracked stock has the same add action disabled; no «Удалить из отслеживаемых» in catalog
  it('tracked stock has disabled add button and no untrack button in catalog', async () => {
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    // No «Удалить из отслеживаемых» text visible
    expect(screen.queryByText('Удалить из отслеживаемых')).not.toBeInTheDocument();
    // The add buttons exist; the one for the tracked stock (AAPL) should be disabled
    const addButtons = screen.getAllByRole('button', { name: /Добавить в отслеживаемые|Акция уже отслеживается/i });
    expect(addButtons.length).toBeGreaterThan(0);
    const disabledAdd = addButtons.find((btn) => (btn as HTMLButtonElement).disabled);
    expect(disabledAdd).toBeDefined();
  });

  // Test 5: Clicking enabled add action calls trackStock with the correct stock ID
  it('clicking enabled add button calls trackStock with untracked stock id', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('BAS').length).toBeGreaterThan(0));
    const enabledAddBtn = screen
      .getAllByRole('button', { name: 'Добавить в отслеживаемые' })
      .find((btn) => !(btn as HTMLButtonElement).disabled);
    expect(enabledAddBtn).toBeDefined();
    await act(async () => { await user.click(enabledAddBtn!); });
    expect(api.trackStock).toHaveBeenCalled();
  });

  // Test 6: Action buttons do not render visible long text labels
  it('action buttons do not render visible long text labels', async () => {
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    // Buttons should not have visible text content matching these labels
    // (tooltips in ant-tooltip-inner are acceptable — they're invisible by default)
    const buttons = screen.queryAllByRole('button');
    const buttonTexts = buttons.map((btn) => btn.textContent?.trim() ?? '');
    expect(buttonTexts).not.toContain('Добавить в отслеживаемые');
    expect(buttonTexts).not.toContain('Удалить из отслеживаемых');
    expect(buttonTexts).not.toContain('Обновить цены');
  });

  // Test 7: 51+ fixtures → first page shows exactly 50 rows, pagination navigates to remainder
  it('paginates with 50 rows per page when catalog has 51+ stocks', async () => {
    const api = await import('../services/api');
    const manyStocks: Stock[] = Array.from({ length: 51 }, (_, i) => ({
      id: i + 100,
      ticker: `T${String(i).padStart(3, '0')}`,
      name: `Stock ${String(i).padStart(3, '0')}`,
      commonName: `Stock ${String(i).padStart(3, '0')}`,
      exchange: 'NYSE',
      currentPrice: 10,
      updatedAt: '2026-08-18T00:00:00Z',
      trackingStatus: 0,
      marketIndexIds: [],
    }));
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: manyStocks });
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('T000').length).toBeGreaterThan(0));
    // Count stock rows on page (tickers T000–T049 are on page 1, T050 on page 2)
    // Each ticker text appears at least once in the table; T050 should not be visible on page 1
    expect(screen.queryByText('T050')).not.toBeInTheDocument();
    expect(screen.getAllByText('T000').length).toBeGreaterThan(0);
    // Pagination shows total
    expect(screen.getByText(/Всего: 51/)).toBeInTheDocument();
  });

  // Test 8: Default order is alphabetical by stock name
  it('renders catalog stocks in alphabetical order by name', async () => {
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    // Apple (commonName) < BASF < BMW alphabetically
    // Check tickers appear in DOM in correct relative order
    const allText = document.body.textContent ?? '';
    const idxApple = allText.indexOf('AAPL');
    const idxBas = allText.indexOf('BAS');
    const idxBmw = allText.indexOf('BMW');
    expect(idxApple).toBeGreaterThanOrEqual(0);
    expect(idxBas).toBeGreaterThanOrEqual(0);
    expect(idxBmw).toBeGreaterThanOrEqual(0);
    expect(idxApple).toBeLessThan(idxBas);
    expect(idxBas).toBeLessThan(idxBmw);
  });

  // Test 10: No countdown/auto-refresh text or bulk refresh action in catalog
  it('does not show auto-refresh countdown or bulk refresh button in catalog mode', async () => {
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect(screen.queryByText(/Авто-обновление через/)).not.toBeInTheDocument();
    expect(screen.queryByText('Обновить цены')).not.toBeInTheDocument();
  });

  // Test 11: Catalog mode creates no automatic quote refresh with fake timers
  it('catalog mode creates no periodic auto-refresh intervals', async () => {
    vi.useFakeTimers();
    const api = await import('../services/api');
    renderPage('catalog');
    await act(async () => {
      await Promise.resolve(); // let state settle
    });
    vi.clearAllMocks();
    // Advance 15 minutes — no getStockPrice should be called
    await act(async () => { vi.advanceTimersByTime(15 * 60 * 1000); });
    expect(api.getStockPrice).not.toHaveBeenCalled();
    vi.useRealTimers();
  });

  // Test 12: Tracked rows have no special portfolio/tracked background class in catalog
  it('tracked rows do not have portfolio-stock-row class in catalog mode', async () => {
    const api = await import('../services/api');
    // Put trackedStock in a portfolio
    vi.mocked(api.getPortfolios).mockResolvedValue({
      data: [{ id: 1, name: 'P', items: [{ stockId: 1, quantity: 1, averagePurchasePrice: 0 }] }],
    });
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    // In catalog mode, no row should have the portfolio highlight class
    const rows = document.querySelectorAll('tr.portfolio-stock-row');
    expect(rows.length).toBe(0);
  });

  it('loads catalog stock history via general endpoint, supports range changes and manual refresh, and keeps tracking action enabled', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    renderPage('catalog');

    await waitFor(() => expect(screen.getAllByText('MCD').length).toBeGreaterThan(0));
    const stockRow = screen.getAllByText('MCD')[0].closest('tr');
    expect(stockRow).not.toBeNull();
    const addButton = within(stockRow as HTMLElement).getByRole('button', { name: 'Добавить в отслеживаемые' });
    expect(addButton).toBeEnabled();

    await user.click(getTickerToggleButton(mcdCatalogStock.id, 'MCD'));

    await waitFor(() => expect(api.getStockHistory).toHaveBeenCalledWith(mcdCatalogStock.id, '1y'));
    expect(await screen.findByText("История цены: MCD — McDonald's Corporation")).toBeInTheDocument();
    expect(screen.getByText('305.00 USD')).toBeInTheDocument();

    await user.click(screen.getByText('6 мес.'));
    await waitFor(() => expect(api.getStockHistory).toHaveBeenCalledWith(mcdCatalogStock.id, '6m'));
    expect(await screen.findByText('330.00 USD')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Перезагрузить историю/ }));
    await user.click(await screen.findByRole('button', { name: 'Перезагрузить' }));

    await waitFor(() => expect(api.refreshStockHistory).toHaveBeenCalledWith(mcdCatalogStock.id));
    await waitFor(() => expect(api.getStockHistory).toHaveBeenLastCalledWith(mcdCatalogStock.id, '6m'));
    expect(api.trackStock).not.toHaveBeenCalled();
    expect(addButton).toBeEnabled();
  }, 30000);

  it('catalog charts create no automatic history or quote refresh timers', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    renderPage('catalog');

    await waitFor(() => expect(screen.getAllByText('MCD').length).toBeGreaterThan(0));
    await user.click(getTickerToggleButton(mcdCatalogStock.id, 'MCD'));
    await waitFor(() => expect(api.getStockHistory).toHaveBeenCalledWith(mcdCatalogStock.id, '1y'));

    vi.useFakeTimers();
    try {
      vi.clearAllMocks();
      await act(async () => { vi.advanceTimersByTime(15 * 60 * 1000); });

      expect(api.getStockHistory).not.toHaveBeenCalled();
      expect(api.refreshStockHistory).not.toHaveBeenCalled();
      expect(api.getStockPrice).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  }, 15000);

  it('catalog uses the general stock history endpoint even when the stock belongs to an index', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getStockCatalog).mockResolvedValue({
      data: [{ ...mcdCatalogStock, id: 5, marketIndexIds: [1] }],
    });
    renderPage('catalog');

    await waitFor(() => expect(screen.getAllByText('MCD').length).toBeGreaterThan(0));
    await user.click(getTickerToggleButton(5, 'MCD'));

    await waitFor(() => expect(api.getStockHistory).toHaveBeenCalledWith(5, '1y'));
    expect(api.getIndexConstituentHistory).not.toHaveBeenCalled();
  }, 15000);

  it('expanding and collapsing the last row on a non-final page never changes the current page', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    const manyStocks: Stock[] = Array.from({ length: 120 }, (_, i) => buildCatalogStock(i));
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: manyStocks });
    vi.mocked(api.getStockHistory).mockResolvedValue({
      data: {
        ...buildHistoryResponse('1y'),
        points: [
          {
            ...buildHistoryResponse('1y').points[0],
            closeRaw: 99,
            closeNormalized: 99,
          },
        ],
      },
    });

    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('T000').length).toBeGreaterThan(0));

    await user.click(getPaginationTrigger(2));
    await waitFor(() => expect(screen.getAllByText('T050').length).toBeGreaterThan(0));
    await waitFor(() => expect(document.querySelector('tr[data-row-key="199"]')).not.toBeNull());
    expect(screen.queryByText('T100')).not.toBeInTheDocument();
    expectActivePage(2);

    await user.click(getTickerToggleButton(199, 'T099'));
    await waitFor(() => expect(api.getStockHistory).toHaveBeenCalledWith(199, '1y'));
    expect(await screen.findByText('История цены: T099 — Stock 099')).toBeInTheDocument();
    expect(screen.queryByText('T100')).not.toBeInTheDocument();
    expectActivePage(2);

    await user.click(getTickerToggleButton(199, 'T099'));
    await waitFor(() =>
      expect(within(document.querySelector('tr[data-row-key="199"]') as HTMLElement).getByRole('button', { name: 'Открыть график цены: T099' })).toBeInTheDocument());
    expect(screen.getAllByText('T050').length).toBeGreaterThan(0);
    expect(screen.queryByText('T100')).not.toBeInTheDocument();
    expectActivePage(2);
  }, 15000);

  it('keeps expansion deterministic through async chart loading and explicit paginator interaction', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    const manyStocks: Stock[] = Array.from({ length: 120 }, (_, i) => buildCatalogStock(i));
    let resolveHistory: ((value: { data: ReturnType<typeof buildHistoryResponse> }) => void) | null = null;
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: manyStocks });
    vi.mocked(api.getStockHistory).mockImplementation(() => new Promise((resolve) => {
      resolveHistory = resolve;
    }));

    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('T000').length).toBeGreaterThan(0));

    await user.click(getPaginationTrigger(2));
    await waitFor(() => expect(screen.getAllByText('T050').length).toBeGreaterThan(0));
    await waitFor(() => expect(document.querySelector('tr[data-row-key="199"]')).not.toBeNull());

    await user.click(getTickerToggleButton(199, 'T099'));
    expectActivePage(2);
    expect(screen.queryByText('T100')).not.toBeInTheDocument();

    resolveHistory?.({ data: buildHistoryResponse('1y') });
    await waitFor(() => expect(screen.getByText('История цены: T099 — Stock 099')).toBeInTheDocument());
    expectActivePage(2);

    await user.click(getPaginationTrigger(3));
    await waitFor(() => expect(screen.getAllByText('T100').length).toBeGreaterThan(0));
    expect(screen.queryByText('T050')).not.toBeInTheDocument();
    expectActivePage(3);
  }, 15000);

  it('keeps or clamps the current page deterministically when filtering changes the catalog size', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    const manyStocks: Stock[] = Array.from({ length: 120 }, (_, i) => buildCatalogStock(i));
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: manyStocks });

    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('T000').length).toBeGreaterThan(0));

    await user.click(getPaginationTrigger(2));
    await waitFor(() => expect(screen.getAllByText('T050').length).toBeGreaterThan(0));
    expectActivePage(2);

    const [catalogSearch] = screen.getAllByPlaceholderText('Поиск: тикер, название, биржа, индекс');
    await user.type(catalogSearch, 'Stock 0');
    await waitFor(() => expect(screen.getAllByText('T050').length).toBeGreaterThan(0));
    expect(screen.queryByText('T100')).not.toBeInTheDocument();
    expectActivePage(2);

    await user.clear(catalogSearch);
    await user.type(catalogSearch, 'Stock 099');
    await waitFor(() => expect(document.querySelector('tr[data-row-key="199"]')).not.toBeNull());
    expect(screen.queryByText('T050')).not.toBeInTheDocument();
    expectActivePage(1);
  }, 15000);
});

// ── Period-performance sorting tests ──────────────────────────────────────────

describe('StocksPage catalog period-performance sorting', () => {
  const buildPerfItem = (stockId: number, changePercent: number | null) => ({
    stockId,
    startPrice: 100,
    endPrice: changePercent != null ? 100 * (1 + changePercent / 100) : null,
    changePercent,
    startAtUtc: '2026-01-01T00:00:00Z',
    endAtUtc: '2026-08-01T00:00:00Z',
    dataStatus: (changePercent != null ? 'Available' : 'InsufficientData') as 'Available' | 'InsufficientData',
  });

  const buildPerfResponse = (range: string, items: ReturnType<typeof buildPerfItem>[]) => ({
    data: { range, generatedAtUtc: '2026-08-19T00:00:00Z', items },
  });

  beforeEach(async () => {
    window.history.replaceState({}, '', '/stocks/catalog');
    vi.clearAllMocks();
    const api = await import('../services/api');
    vi.mocked(api.getTrackedStocks).mockResolvedValue({ data: [] });
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: [trackedStock, untrackedStock, untrackedStockFRA, mcdCatalogStock] });
    vi.mocked(api.getStockHistory).mockResolvedValue({ data: buildHistoryResponse('1y') });
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue({ data: { range: '1y', generatedAtUtc: '2026-08-19T00:00:00Z', items: [] } });
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  // ─ Contract tests ────────────────────────────────────────────────────────────

  it('period options match the shared STOCK_HISTORY_RANGE_OPTIONS used by indices', () => {
    expect(CATALOG_SORT_MODE_OPTIONS[0]).toEqual({ label: 'По названию', value: CATALOG_SORT_NAME_MODE });
    expect(CATALOG_SORT_MODE_OPTIONS.slice(1)).toEqual(STOCK_HISTORY_RANGE_OPTIONS);
    expect(STOCK_HISTORY_RANGE_OPTIONS.map((o) => o.value)).toEqual([
      'today', '24h', '1w', '1m', '3m', '6m', '1y', '3y', '5y',
    ]);
  });

  it('isCatalogPeriodSortMode distinguishes name mode from period mode', () => {
    expect(isCatalogPeriodSortMode(CATALOG_SORT_NAME_MODE)).toBe(false);
    expect(isCatalogPeriodSortMode('1y')).toBe(true);
    expect(isCatalogPeriodSortMode('6m')).toBe(true);
    expect(isCatalogPeriodSortMode('today')).toBe(true);
  });

  // ─ Default mode ───────────────────────────────────────────────────────────────

  it('defaults to name sort mode with no performance column visible', async () => {
    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect(screen.queryByText('Рост за период')).not.toBeInTheDocument();
    const api = await import('../services/api');
    expect(vi.mocked(api.getStockCatalogPerformance)).not.toHaveBeenCalled();
  });

  // ─ Descending sort ────────────────────────────────────────────────────────────

  it('sorts stocks by period performance descending, nulls last', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    // id: 1 AAPL +5%, id: 2 BAS -3%, id: 3 BMW null, id: 4 MCD +15%
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue(buildPerfResponse('1y', [
      buildPerfItem(1, 5),
      buildPerfItem(2, -3),
      buildPerfItem(3, null),
      buildPerfItem(4, 15),
    ]));

    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    const opt1y = await screen.findByText('1 год');
    await user.click(opt1y);

    await waitFor(() => expect(screen.getAllByText('Рост за период')[0]).toBeInTheDocument());
    await waitFor(() => expect(vi.mocked(api.getStockCatalogPerformance)).toHaveBeenCalledWith('1y', expect.any(AbortSignal)));

    // Verify performance column shows formatted percentages
    await waitFor(() => {
      expect(screen.getByText('+15,00 %')).toBeInTheDocument();
      expect(screen.getByText('+5,00 %')).toBeInTheDocument();
      expect(screen.getByText('-3,00 %')).toBeInTheDocument();
    });

    // Verify order: MCD (+15%) → AAPL (+5%) → BAS (-3%) → BMW (null)
    const rows = Array.from(document.querySelectorAll('tr[data-row-key]'));
    const ids = rows.map((r) => r.getAttribute('data-row-key'));
    expect(ids.indexOf('4')).toBeLessThan(ids.indexOf('1'));
    expect(ids.indexOf('1')).toBeLessThan(ids.indexOf('2'));
    expect(ids.indexOf('2')).toBeLessThan(ids.indexOf('3'));
  });

  it('keeps one global 24h performance order regardless of index membership', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    const daxMember: Stock = {
      id: 10,
      ticker: 'DAXA',
      name: 'DAX Member',
      commonName: 'DAX Member',
      exchange: 'Frankfurt',
      currentPrice: 10,
      updatedAt: '2026-08-19T00:00:00Z',
      trackingStatus: 0,
      marketIndexIds: [2],
    };
    const otherIndexMember: Stock = {
      id: 11,
      ticker: 'EURO',
      name: 'Euro Member',
      commonName: 'Euro Member',
      exchange: 'NYSE',
      currentPrice: 10,
      updatedAt: '2026-08-19T00:00:00Z',
      trackingStatus: 0,
      marketIndexIds: [3],
    };
    const noIndexStock: Stock = {
      id: 12,
      ticker: 'NONE',
      name: 'No Index',
      commonName: 'No Index',
      exchange: 'NYSE',
      currentPrice: 10,
      updatedAt: '2026-08-19T00:00:00Z',
      trackingStatus: 0,
      marketIndexIds: [],
    };
    const nullPerformanceStock: Stock = {
      id: 13,
      ticker: 'NUL',
      name: 'Null Perf',
      commonName: 'Null Perf',
      exchange: 'Frankfurt',
      currentPrice: 10,
      updatedAt: '2026-08-19T00:00:00Z',
      trackingStatus: 0,
      marketIndexIds: [2],
    };
    vi.mocked(api.getStockCatalog).mockResolvedValue({
      data: [daxMember, otherIndexMember, noIndexStock, nullPerformanceStock],
    });
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue(buildPerfResponse('24h', [
      buildPerfItem(10, -1),
      buildPerfItem(11, 9),
      buildPerfItem(12, 4),
      buildPerfItem(13, null),
    ]));

    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('DAXA').length).toBeGreaterThan(0));

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('24 ч.'));
    await waitFor(() => expect(vi.mocked(api.getStockCatalogPerformance)).toHaveBeenCalledWith('24h', expect.any(AbortSignal)));

    const rows = Array.from(document.querySelectorAll('tr[data-row-key]'));
    const ids = rows.map((r) => r.getAttribute('data-row-key'));
    expect(ids.indexOf('11')).toBeLessThan(ids.indexOf('12'));
    expect(ids.indexOf('12')).toBeLessThan(ids.indexOf('10'));
    expect(ids.indexOf('10')).toBeLessThan(ids.indexOf('13'));
  });

  // ─ Ascending sort ─────────────────────────────────────────────────────────────

  it('switches to ascending when direction toggle is clicked', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue(buildPerfResponse('1y', [
      buildPerfItem(1, 5),
      buildPerfItem(2, -3),
      buildPerfItem(3, null),
      buildPerfItem(4, 15),
    ]));

    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));
    await waitFor(() => expect(vi.mocked(api.getStockCatalogPerformance)).toHaveBeenCalled());

    // Toggle direction to ascending
    const dirBtn = screen.getByRole('button', { name: 'Сортировать по возрастанию' });
    await user.click(dirBtn);

    // Ascending: BAS (-3%) → AAPL (+5%) → MCD (+15%), nulls still last
    const rows = Array.from(document.querySelectorAll('tr[data-row-key]'));
    const ids = rows.map((r) => r.getAttribute('data-row-key'));
    expect(ids.indexOf('2')).toBeLessThan(ids.indexOf('1'));
    expect(ids.indexOf('1')).toBeLessThan(ids.indexOf('4'));
    expect(ids.indexOf('3')).toBeGreaterThan(ids.indexOf('4'));
  });

  // ─ Tie-breaking ──────────────────────────────────────────────────────────────

  it('breaks ties by stock id ascending for both available and null values', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue(buildPerfResponse('1y', [
      buildPerfItem(1, 10),
      buildPerfItem(2, 10),
      buildPerfItem(3, null),
      buildPerfItem(4, 10),
    ]));

    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));
    await waitFor(() => expect(vi.mocked(api.getStockCatalogPerformance)).toHaveBeenCalled());

    const rows = Array.from(document.querySelectorAll('tr[data-row-key]'));
    const ids = rows.map((r) => r.getAttribute('data-row-key'));
    // Tied values sort by stockId asc: 1, 2, 4 then null (3) last
    expect(ids.indexOf('1')).toBeLessThan(ids.indexOf('2'));
    expect(ids.indexOf('2')).toBeLessThan(ids.indexOf('4'));
    expect(ids.indexOf('3')).toBeGreaterThan(ids.indexOf('4'));
  });

  // ─ Stale response rejection ──────────────────────────────────────────────────

  it('rejects stale performance responses when period is changed quickly', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');

    let resolve1y: ((v: unknown) => void) | null = null;
    const items6m = [buildPerfItem(1, 20), buildPerfItem(2, 10), buildPerfItem(3, 5), buildPerfItem(4, 1)];

    vi.mocked(api.getStockCatalogPerformance).mockImplementation((range) => {
      if (range === '1y') {
        return new Promise((resolve) => { resolve1y = resolve; });
      }
      return Promise.resolve(buildPerfResponse('6m', items6m));
    });

    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });

    // Select 1y (slow request - pending)
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));

    // Quickly switch to 6m before 1y resolves
    await user.click(sortSelect);
    await user.click(await screen.findByText('6 мес.'));
    await waitFor(() => expect(screen.getByText('+20,00 %')).toBeInTheDocument());

    // Now resolve the 1y request (stale response)
    act(() => {
      resolve1y?.(buildPerfResponse('1y', [
        buildPerfItem(1, 1),
        buildPerfItem(2, 2),
        buildPerfItem(3, 3),
        buildPerfItem(4, 4),
      ]));
    });
    // The stale 1y data must not replace the current 6m data
    await waitFor(() => expect(screen.getByText('+20,00 %')).toBeInTheDocument());
    // +4,00 % would only appear if stale 1y data for stock 4 (+4%) replaced 6m data (+1%)
    expect(screen.queryByText('+4,00 %')).not.toBeInTheDocument();
  }, 15000);

  // ─ No N+1 requests ────────────────────────────────────────────────────────────

  it('issues a single batch request for 600+ stocks, not per-stock', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    const bigCatalog = Array.from({ length: 600 }, (_, i) => ({
      id: i + 1,
      ticker: `T${i}`,
      name: `Stock ${i}`,
      commonName: `Stock ${i}`,
      exchange: 'NYSE',
      currentPrice: 10,
      updatedAt: '2026-08-19T00:00:00Z',
      trackingStatus: 0 as const,
      marketIndexIds: [],
    }));
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: bigCatalog });
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue(buildPerfResponse('1y', []));

    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('T0').length).toBeGreaterThan(0));

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));
    await waitFor(() => expect(vi.mocked(api.getStockCatalogPerformance)).toHaveBeenCalled());

    // Only one batch request, not 600 individual requests
    expect(vi.mocked(api.getStockCatalogPerformance)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(api.getStockHistory)).not.toHaveBeenCalled();
  }, 15000);

  // ─ Pagination resets on period change, preserves on direction toggle ──────────

  it('resets page to 1 when period changes but preserves page when toggling direction', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    const manyStocks = Array.from({ length: 120 }, (_, i) => ({
      id: i + 1,
      ticker: `T${String(i).padStart(3, '0')}`,
      name: `Stock ${String(i).padStart(3, '0')}`,
      commonName: `Stock ${String(i).padStart(3, '0')}`,
      exchange: 'NYSE',
      currentPrice: 10,
      updatedAt: '2026-08-19T00:00:00Z',
      trackingStatus: 0 as const,
      marketIndexIds: [],
    }));
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: manyStocks });
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue(buildPerfResponse('1y', []));

    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('T000').length).toBeGreaterThan(0));

    // Navigate to page 2
    const page2Trigger = document.querySelector('li.ant-pagination-item-2 a, li.ant-pagination-item-2 button');
    expect(page2Trigger).not.toBeNull();
    await user.click(page2Trigger as HTMLElement);
    await waitFor(() => expect(screen.getAllByText('T050').length).toBeGreaterThan(0));
    expect(document.querySelector('li.ant-pagination-item-2.ant-pagination-item-active')).not.toBeNull();

    // Change period — should reset to page 1
    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));
    await waitFor(() => {
      expect(document.querySelector('li.ant-pagination-item-1.ant-pagination-item-active')).not.toBeNull();
    });

    // Navigate back to page 2
    await user.click(document.querySelector('li.ant-pagination-item-2 a, li.ant-pagination-item-2 button') as HTMLElement);
    await waitFor(() => expect(screen.getAllByText('T050').length).toBeGreaterThan(0));
    expect(document.querySelector('li.ant-pagination-item-2.ant-pagination-item-active')).not.toBeNull();

    // Toggle direction — page stays at 2
    const dirBtn = screen.getByRole('button', { name: 'Сортировать по возрастанию' });
    await user.click(dirBtn);
    expect(document.querySelector('li.ant-pagination-item-2.ant-pagination-item-active')).not.toBeNull();
  }, 15000);

  // ─ Expanding last row on non-final page preserves page and sort order ─────────

  it('expanding/collapsing the last row on a non-final page does not change page or sort order', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    const manyStocks = Array.from({ length: 101 }, (_, i) => ({
      id: i + 1,
      ticker: `T${String(i).padStart(3, '0')}`,
      name: `Stock ${String(i).padStart(3, '0')}`,
      commonName: `Stock ${String(i).padStart(3, '0')}`,
      exchange: 'NYSE',
      currentPrice: 10,
      updatedAt: '2026-08-19T00:00:00Z',
      trackingStatus: 0 as const,
      marketIndexIds: [],
    }));
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: manyStocks });
    vi.mocked(api.getStockHistory).mockResolvedValue({ data: buildHistoryResponse('1y') });
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue(buildPerfResponse('1y',
      manyStocks.map((s, i) => buildPerfItem(s.id, i * 0.1)),
    ));

    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('T000').length).toBeGreaterThan(0));

    // Select period sort
    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));
    await waitFor(() => expect(vi.mocked(api.getStockCatalogPerformance)).toHaveBeenCalled());

    // Go to page 2
    const page2Trigger = document.querySelector('li.ant-pagination-item-2 a, li.ant-pagination-item-2 button');
    expect(page2Trigger).not.toBeNull();
    await user.click(page2Trigger as HTMLElement);
    await waitFor(() => {
      expect(document.querySelector('li.ant-pagination-item-2.ant-pagination-item-active')).not.toBeNull();
    });
    await waitFor(() => expect(screen.getAllByText('T049').length).toBeGreaterThan(0));

    // Expand the last row on page 2 (stock T049, id=50)
    const row50 = document.querySelector('tr[data-row-key="50"]');
    expect(row50).not.toBeNull();
    const toggleBtn = within(row50 as HTMLElement).queryByRole('button', { name: /Открыть график цены: T049/ });
    expect(toggleBtn).not.toBeNull();
    await user.click(toggleBtn as HTMLElement);

    expect(document.querySelector('li.ant-pagination-item-2.ant-pagination-item-active')).not.toBeNull();
    expect(screen.queryByText('T100')).not.toBeInTheDocument();

    // Collapse
    const collapseBtn = within(row50 as HTMLElement).queryByRole('button', { name: /Закрыть график цены: T049/ });
    expect(collapseBtn).not.toBeNull();
    await user.click(collapseBtn as HTMLElement);

    expect(document.querySelector('li.ant-pagination-item-2.ant-pagination-item-active')).not.toBeNull();
  }, 15000);

  // ─ Return to name sort clears performance state ───────────────────────────────

  it('switching back to name sort clears performance column and does not call API again', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue(buildPerfResponse('1y', [
      buildPerfItem(1, 5),
    ]));

    render(
      <MemoryRouter>
        <AuthContext.Provider value={{ token: 'token', user: { id: 1, username: 'test', email: 'x@x.com', roles: [] }, login: () => {}, logout: () => {}, refreshUser: async () => {}, isAuthenticated: true, loading: false }}>
          <StocksPage mode="catalog" />
        </AuthContext.Provider>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));
    await waitFor(() => expect(screen.getAllByText('Рост за период')[0]).toBeInTheDocument());

    const callCount = vi.mocked(api.getStockCatalogPerformance).mock.calls.length;

    // Switch back to name sort
    await user.click(sortSelect);
    await user.click(await screen.findByText('По названию'));

    await waitFor(() => expect(screen.queryByText('Рост за период')).not.toBeInTheDocument());
    expect(vi.mocked(api.getStockCatalogPerformance).mock.calls.length).toBe(callCount);
  });

  it('applies advanced filters from drawer and supports external reset without clearing search', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    window.history.replaceState({}, '', '/stocks/catalog');
    vi.mocked(api.getSectors).mockResolvedValue([
      {
        id: 15,
        name: 'Information Technology',
        normalizedName: 'INFORMATION TECHNOLOGY',
        isArchived: false,
        sortOrder: 1,
        createdAtUtc: '',
        updatedAtUtc: '',
        industryCount: 1,
        stockCount: 1,
        industries: [{ id: 1501, sectorId: 15, name: 'Software', normalizedName: 'SOFTWARE', isArchived: false, sortOrder: 1, createdAtUtc: '', updatedAtUtc: '', stockCount: 1 }],
      },
      {
        id: 20,
        name: 'Materials',
        normalizedName: 'MATERIALS',
        isArchived: false,
        sortOrder: 2,
        createdAtUtc: '',
        updatedAtUtc: '',
        industryCount: 1,
        stockCount: 1,
        industries: [{ id: 2001, sectorId: 20, name: 'Chemicals', normalizedName: 'CHEMICALS', isArchived: false, sortOrder: 1, createdAtUtc: '', updatedAtUtc: '', stockCount: 1 }],
      },
    ]);

    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const searchInput = screen.getByPlaceholderText('Поиск: тикер, название, биржа, индекс');
    await user.type(searchInput, 'A');

    await user.click(screen.getByRole('button', { name: 'Открыть расширенные фильтры' }));
    await user.click(screen.getByRole('combobox', { name: 'Фильтр по бирже' }));
    await user.click(await screen.findByText('Frankfurt (FRA)'));
    await user.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => expect(screen.queryByText('AAPL')).not.toBeInTheDocument());
    expect(screen.getAllByText('BAS').length).toBeGreaterThan(0);
    expect(screen.getByText('Фильтры (1)')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Сбросить расширенные фильтры' }));
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect((searchInput as HTMLInputElement).value).toBe('A');
  });

  it('resets catalog pagination to page 1 when advanced filters are applied', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    window.history.replaceState({}, '', '/stocks/catalog');
    const fiftyOneStocks = Array.from({ length: 51 }, (_, index) => ({
      ...buildCatalogStock(index + 1),
      exchange: index === 50 ? 'Frankfurt' : 'NYSE',
    }));
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: fiftyOneStocks });
    vi.mocked(api.getSectors).mockResolvedValue([]);

    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('T001').length).toBeGreaterThan(0));

    const page2Trigger = document.querySelector('li.ant-pagination-item-2 a, li.ant-pagination-item-2 button');
    expect(page2Trigger).not.toBeNull();
    await user.click(page2Trigger as HTMLElement);
    expect(document.querySelector('li.ant-pagination-item-2.ant-pagination-item-active')).not.toBeNull();

    await user.click(screen.getByRole('button', { name: 'Открыть расширенные фильтры' }));
    await user.click(screen.getByRole('combobox', { name: 'Фильтр по бирже' }));
    await user.click(await screen.findByText('Frankfurt (FRA)'));
    await user.click(screen.getByRole('button', { name: 'Применить' }));

    await waitFor(() => {
      expect(document.querySelector('li.ant-pagination-item-1.ant-pagination-item-active')).not.toBeNull();
    });
  });
});

describe('StocksPage tracked mode regression', () => {
  beforeEach(async () => {
    window.history.replaceState({}, '', '/stocks');
    vi.clearAllMocks();
    const api = await import('../services/api');
    vi.mocked(api.getTrackedStocks).mockResolvedValue({ data: [trackedStock] });
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: [trackedStock, untrackedStock] });
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  // Test 14: Tracked page retains non-destructive untrack action (guarded by Popconfirm confirmation)
  it('tracked page shows non-destructive untrack action', async () => {
    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    // The untrack icon-button must be present in tracked mode
    await waitFor(() => {
      const deleteBtn = document.querySelector('[aria-label="Удалить из отслеживаемых"]'); // exact aria-label
      expect(deleteBtn).not.toBeNull();
    });
  });

  it('tracked page keeps untrack confirmation flow and calls untrackStock once', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getPortfolios).mockResolvedValue({ data: [] });
    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const deleteButton = screen
      .getAllByRole('button', { name: 'Удалить из отслеживаемых' })
      .find((button) => !(button as HTMLButtonElement).disabled);
    expect(deleteButton).toBeDefined();
    await user.click(deleteButton as HTMLElement);
    await screen.findByText('Удалить из отслеживаемых? Акция останется в «Список акций», индексах и портфелях.');
    await user.click(await screen.findByRole('button', { name: 'Да' }));
    await waitFor(() => expect(api.untrackStock).toHaveBeenCalledTimes(1));
    expect(api.untrackStock).toHaveBeenCalledWith(trackedStock.id);
  });

  // Tracked page shows countdown and auto-refresh
  it('tracked page shows auto-refresh countdown', async () => {
    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    const countdownEls = screen.queryAllByText(/Авто-обновление через/);
    expect(countdownEls.length).toBeGreaterThan(0);
    const refreshBtns = screen.queryAllByText('Обновить цены');
    expect(refreshBtns.length).toBeGreaterThan(0);
  });

  // Tracked page retains exchange grouping
  it('tracked page retains exchange-grouped tables', async () => {
    const api = await import('../services/api');
    vi.mocked(api.getTrackedStocks).mockResolvedValue({
      data: [
        { ...trackedStock, exchange: 'Frankfurt' },
      ],
    });
    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    // FRA appears both as group heading and as exchange tag — either confirms grouping is active
    await waitFor(() => {
      expect(screen.getAllByText('FRA').length).toBeGreaterThan(0);
    });
  });

  it('renders tracked layout with header add action and separate toolbar row containing refresh controls', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue({ data: { range: '24h', generatedAtUtc: '2026-08-19T00:00:00Z', items: [] } });
    vi.mocked(api.getStockPrice).mockResolvedValue({
      data: {
        ticker: 'AAPL',
        exchange: 'NYSE',
        currentPrice: 101,
        previousClose: 100,
        change: 1,
        changePercent: 1,
        timestamp: '2026-08-19T00:00:00Z',
        marketState: 'CLOSED',
      },
    } as never);

    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const headerActions = screen.getByTestId('stocks-page-header-actions');
    const toolbar = screen.getByTestId('stocks-page-toolbar-row');
    const addButton = screen.getByRole('button', { name: /Добавить акцию/i });
    expect(headerActions).toContainElement(addButton);
    expect(toolbar).not.toContainElement(addButton);
    expectRenderedBefore(headerActions, toolbar);
    expect(toolbar).toHaveStyle({ flexWrap: 'wrap' });

    expect(screen.getByPlaceholderText('Поиск: тикер, название, биржа, индекс')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Открыть расширенные фильтры' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Сортировка' })).toBeInTheDocument();
    expect(screen.getByText(/Авто-обновление через/)).toBeInTheDocument();
    const refreshButton = screen.getByRole('button', { name: /Обновить цены/i });
    expect(refreshButton).toBeInTheDocument();

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('24 ч.'));
    expect(screen.getByRole('button', { name: 'Сортировать по возрастанию' })).toBeInTheDocument();
    expect(vi.mocked(api.getStockCatalogPerformance)).toHaveBeenCalledWith('24h', expect.any(AbortSignal));

    await user.click(refreshButton);
    await waitFor(() => expect(vi.mocked(api.getStockPrice)).toHaveBeenCalled());

    await user.click(addButton);
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
  });

  it('tracked search filters by ticker, name/common name, exchange and index name', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getMarketIndices).mockResolvedValue([
      {
        id: 1,
        name: 'S&P 500',
        code: 'SPX',
        providerSymbol: null,
        description: '',
        countryOrRegion: '',
        sortOrder: 1,
        isArchived: false,
        showInNavigation: true,
      },
      {
        id: 2,
        name: 'DAX',
        code: 'DAX',
        providerSymbol: null,
        description: '',
        countryOrRegion: '',
        sortOrder: 2,
        isArchived: false,
        showInNavigation: true,
      },
    ]);
    vi.mocked(api.getTrackedStocks).mockResolvedValue({
      data: [
        { ...trackedStock, ticker: 'AAPL', name: 'Apple Inc.', commonName: 'Apple', exchange: 'NYSE', marketIndexIds: [1] },
        { ...untrackedStock, id: 5, ticker: 'BASF', name: 'BASF SE', commonName: 'Chemicals', exchange: 'Frankfurt', trackingStatus: 1, marketIndexIds: [2] },
      ],
    });

    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    const searchInput = screen.getByPlaceholderText('Поиск: тикер, название, биржа, индекс');

    await user.type(searchInput, 'BASF');
    await waitFor(() => expect(screen.queryByText('AAPL')).not.toBeInTheDocument());
    expect(screen.getAllByText('BASF').length).toBeGreaterThan(0);

    await user.clear(searchInput);
    await user.type(searchInput, 'apple');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect(screen.queryByText('BASF')).not.toBeInTheDocument();

    await user.clear(searchInput);
    await user.type(searchInput, 'frankfurt');
    await waitFor(() => expect(screen.getAllByText('BASF').length).toBeGreaterThan(0));
    expect(screen.queryByText('AAPL')).not.toBeInTheDocument();

    await user.clear(searchInput);
    await user.type(searchInput, 's&p');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect(screen.queryByText('BASF')).not.toBeInTheDocument();
  });

  it('tracked advanced filters and reset work and never reveal catalog-only stocks', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getSectors).mockResolvedValue([
      {
        id: 15,
        name: 'Information Technology',
        normalizedName: 'INFORMATION TECHNOLOGY',
        isArchived: false,
        sortOrder: 1,
        createdAtUtc: '',
        updatedAtUtc: '',
        industryCount: 1,
        stockCount: 1,
        industries: [{ id: 1501, sectorId: 15, name: 'Software', normalizedName: 'SOFTWARE', isArchived: false, sortOrder: 1, createdAtUtc: '', updatedAtUtc: '', stockCount: 1 }],
      },
      {
        id: 20,
        name: 'Materials',
        normalizedName: 'MATERIALS',
        isArchived: false,
        sortOrder: 2,
        createdAtUtc: '',
        updatedAtUtc: '',
        industryCount: 1,
        stockCount: 1,
        industries: [{ id: 2001, sectorId: 20, name: 'Chemicals', normalizedName: 'CHEMICALS', isArchived: false, sortOrder: 1, createdAtUtc: '', updatedAtUtc: '', stockCount: 1 }],
      },
    ]);
    vi.mocked(api.getTrackedStocks).mockResolvedValue({
      data: [
        { ...trackedStock, ticker: 'AAPL', exchange: 'NYSE', trackingStatus: 1 },
        { ...untrackedStock, id: 6, ticker: 'BAS', exchange: 'Frankfurt', trackingStatus: 1 },
      ],
    });
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: [{ ...untrackedStock, id: 88, ticker: 'CATONLY', trackingStatus: 0 }] });

    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect(screen.queryByText('CATONLY')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Открыть расширенные фильтры' }));
    await user.click(screen.getByRole('combobox', { name: 'Фильтр по бирже' }));
    await user.click(await screen.findByText('Frankfurt (FRA)'));
    await user.click(screen.getByRole('button', { name: 'Применить' }));
    await waitFor(() => expect(screen.queryByText('AAPL')).not.toBeInTheDocument());
    expect(screen.getAllByText('BAS').length).toBeGreaterThan(0);
    expect(screen.getByText('Фильтры (1)')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Сбросить расширенные фильтры' }));
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect(screen.queryByText('CATONLY')).not.toBeInTheDocument();
  });

  it('tracked period sorting keeps nulls last, supports direction toggle, and rejects stale responses', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    vi.mocked(api.getTrackedStocks).mockResolvedValue({
      data: [
        { ...trackedStock, id: 1, ticker: 'AAPL', exchange: 'Frankfurt', trackingStatus: 1 },
        { ...trackedStock, id: 2, ticker: 'BAS', name: 'BASF SE', commonName: 'BASF', exchange: 'Frankfurt', trackingStatus: 1 },
        { ...trackedStock, id: 3, ticker: 'BMW', name: 'BMW AG', commonName: 'BMW', exchange: 'Frankfurt', trackingStatus: 1 },
      ],
    });

    let resolve1y: ((value: unknown) => void) | null = null;
    vi.mocked(api.getStockCatalogPerformance).mockImplementation((range) => {
      if (range === '1y') {
        return new Promise((resolve) => { resolve1y = resolve; });
      }
      return Promise.resolve({
        data: {
          range,
          generatedAtUtc: '2026-08-19T00:00:00Z',
          items: [
            { stockId: 1, startPrice: 100, endPrice: 120, changePercent: 20, startAtUtc: '', endAtUtc: '', dataStatus: 'Available' as const },
            { stockId: 2, startPrice: 100, endPrice: 105, changePercent: 5, startAtUtc: '', endAtUtc: '', dataStatus: 'Available' as const },
            { stockId: 3, startPrice: 100, endPrice: null, changePercent: null, startAtUtc: '', endAtUtc: '', dataStatus: 'InsufficientData' as const },
          ],
        },
      });
    });

    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));

    const sortSelect = screen.getByRole('combobox', { name: 'Сортировка' });
    await user.click(sortSelect);
    await user.click(await screen.findByText('1 год'));
    await user.click(sortSelect);
    await user.click(await screen.findByText('6 мес.'));

    await waitFor(() => expect(screen.getByText('+20,00 %')).toBeInTheDocument());
    act(() => {
      resolve1y?.({
        data: {
          range: '1y',
          generatedAtUtc: '2026-08-19T00:00:00Z',
          items: [
            { stockId: 1, startPrice: 100, endPrice: 101, changePercent: 1, startAtUtc: '', endAtUtc: '', dataStatus: 'Available' as const },
            { stockId: 2, startPrice: 100, endPrice: 102, changePercent: 2, startAtUtc: '', endAtUtc: '', dataStatus: 'Available' as const },
            { stockId: 3, startPrice: 100, endPrice: 103, changePercent: 3, startAtUtc: '', endAtUtc: '', dataStatus: 'Available' as const },
          ],
        },
      });
    });

    await waitFor(() => expect(screen.getByText('+20,00 %')).toBeInTheDocument());
    expect(screen.queryByText('+3,00 %')).not.toBeInTheDocument();

    const rowsDesc = getRenderedRowIds();
    expect(rowsDesc.indexOf('1')).toBeLessThan(rowsDesc.indexOf('2'));
    expect(rowsDesc.indexOf('2')).toBeLessThan(rowsDesc.indexOf('3'));

    await user.click(screen.getByRole('button', { name: 'Сортировать по возрастанию' }));
    await waitFor(() => {
      const rowsAsc = getRenderedRowIds();
      expect(rowsAsc.indexOf('2')).toBeLessThan(rowsAsc.indexOf('1'));
      expect(rowsAsc.indexOf('3')).toBeGreaterThan(rowsAsc.indexOf('1'));
    });
  }, 15000);

  it('tracked URL state restores controls via popstate and keeps catalog defaults independent', async () => {
    const user = userEvent.setup();
    const api = await import('../services/api');
    window.history.replaceState({}, '', '/stocks');
    vi.mocked(api.getSectors).mockResolvedValue([]);
    vi.mocked(api.getStockCatalogPerformance).mockResolvedValue({ data: { range: '1y', generatedAtUtc: '2026-08-19T00:00:00Z', items: [] } });

    renderPage('tracked');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    const searchInput = screen.getByPlaceholderText('Поиск: тикер, название, биржа, индекс');
    await user.type(searchInput, 'AAPL');
    await user.click(screen.getByRole('combobox', { name: 'Сортировка' }));
    await user.click(await screen.findByText('1 год'));
    await user.click(screen.getByRole('button', { name: 'Сортировать по возрастанию' }));

    const withTrackedState = window.location.search;
    expect(withTrackedState).toContain('tq=AAPL');
    expect(withTrackedState).toContain('tsort=1y');
    expect(withTrackedState).toContain('tdir=asc');

    window.history.pushState({}, '', '/stocks');
    window.dispatchEvent(new PopStateEvent('popstate'));
    await waitFor(() => expect((screen.getByPlaceholderText('Поиск: тикер, название, биржа, индекс') as HTMLInputElement).value).toBe(''));

    window.history.pushState({}, '', `/stocks${withTrackedState}`);
    window.dispatchEvent(new PopStateEvent('popstate'));
    await waitFor(() => expect((screen.getByPlaceholderText('Поиск: тикер, название, биржа, индекс') as HTMLInputElement).value).toBe('AAPL'));
    expect(screen.getByRole('button', { name: 'Сортировать по убыванию' })).toBeInTheDocument();

    cleanup();
    window.history.replaceState({}, '', `/stocks/catalog${withTrackedState}`);
    renderPage('catalog');
    await waitFor(() => expect(screen.getAllByText('AAPL').length).toBeGreaterThan(0));
    expect((screen.getByPlaceholderText('Поиск: тикер, название, биржа, индекс') as HTMLInputElement).value).toBe('');
    expect(screen.queryByText('Рост за период')).not.toBeInTheDocument();
  });
});
