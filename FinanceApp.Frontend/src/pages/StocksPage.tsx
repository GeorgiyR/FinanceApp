import React, { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import {
  Table,
  Button,
  Spin,
  Typography,
  Popconfirm,
  message,
  Tag,
  Tooltip,
  Input,
  Modal,
  Select,
  Space,
  Alert,
} from 'antd';
import axios from 'axios';
import {
  PlusOutlined,
  EditOutlined,
  DeleteOutlined,
  ReloadOutlined,
  CaretRightFilled,
  FundOutlined,
  StarOutlined,
  SortAscendingOutlined,
  SortDescendingOutlined,
  InfoCircleOutlined,
} from '@ant-design/icons';
import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
import {
  getStockCatalog,
  getStockCatalogPerformance,
  createStock,
  updateStockEdit,
  updateStockQuote,
  getTrackedStocks,
  getPortfolios,
  getStockPrice,
  trackStock,
  untrackStock,
  deleteStockPermanent,
} from '../services/api';
import AuthenticatedShell from '../components/AuthenticatedShell';
import StockEditModal, {
  buildCreateStockPayload,
  buildUpdateStockMetadataPayload,
  loadStockMetadataLookups,
} from '../components/StockEditModal';
import StockPriceChart from '../components/StockPriceChart';
import StockFundamentalsDrawer from '../components/StockFundamentalsDrawer';
import StockExchangeTag from '../components/StockExchangeTag';
import StockClassificationBadges from '../components/StockClassificationBadges';
import { useAuth } from '../contexts/AuthContext';
import type {
  Portfolio,
  MarketIndex,
  SectorDto,
  Stock,
  StockHistoryRange,
  StockTrackingStatus,
  StockQuoteResponse,
  StockMutationBlockedResponse,
  StockDependencyBlockerResponse,
  UpdateStockQuoteRequest,
} from '../types';
import { isQuoteDelayed } from '../utils/quote';
import { applyPersistedQuoteSnapshot, buildQuotePatch } from '../utils/quotePersistence';
import {
  resolveNewestCurrentPriceSnapshot,
  shouldKeepLiveOverlayAfterPersistedRefresh,
} from '../utils/currentPriceSnapshot';
import { formatCurrency as fmtCur, formatPercent } from '../utils/currency';
import { STOCK_HISTORY_RANGE_OPTIONS } from '../components/historyRangeOptions';
import { formatPerformance } from '../components/performanceHelpers';
import type { PerformanceMap } from '../components/performanceHelpers';
import AdvancedStockFiltersDrawer from '../components/stock-filters/AdvancedStockFiltersDrawer';
import AdvancedStockFilterToolbarControls from '../components/stock-filters/AdvancedStockFilterToolbarControls';
import {
  buildIndustryToSectorMap,
  matchesStockAdvancedFilters,
} from '../components/stock-filters/stockFilterEngine';
import {
  areAdvancedStockFiltersEqual,
  countActiveAdvancedFilterGroups,
  EMPTY_ADVANCED_STOCK_FILTERS,
  normalizeAdvancedStockFilters,
  type AdvancedStockFilters,
} from '../components/stock-filters/stockFilterModel';
import {
  getActiveSectorOptions,
  getIndustryOptionsForSelectedSectors,
  normalizeAdvancedStockFiltersWithDirectory,
  parseAdvancedStockFiltersFromSearchParams,
  pruneInvalidIndustrySelections,
  serializeAdvancedStockFiltersToSearchParams,
} from '../components/stock-filters/stockFilterUrlState';

export {
  buildCreateStockPayload,
  buildUpdateStockMetadataPayload,
  IDENTITY_IMMUTABLE_HELPER,
  STOCK_MARKET_INDEX_SELECT_MODE,
} from '../components/StockEditModal';

dayjs.extend(utc);

const { Title, Text } = Typography;

const AUTO_REFRESH_INTERVAL = 10 * 60; // 10 minutes in seconds (tracked page only)
const CATALOG_PAGE_SIZE = 50;

const COLOR_POSITIVE = '#389e0d';
const COLOR_NEGATIVE = '#cf1322';
const PORTFOLIO_ROW_CLASS = 'portfolio-stock-row';
export const STOCK_DELETE_TOOLTIP = 'Удалить из отслеживаемых';
export const PROTECTED_STOCK_DELETE_TOOLTIP = 'Акцию нельзя удалить из отслеживаемых, пока она находится в портфеле';
const STOCK_DELETE_GENERIC_ERROR = 'Ошибка удаления из отслеживаемых';
const STOCK_PERMANENT_DELETE_GENERIC_ERROR = 'Ошибка полного удаления акции';

export const getStockDeleteErrorMessage = (err: unknown): string => {
  if (axios.isAxiosError(err) && typeof err.response?.data === 'string' && err.response.data.trim().length > 0) {
    return err.response.data;
  }

  return STOCK_DELETE_GENERIC_ERROR;
};

const getStockMutationBlockedResponse = (err: unknown): StockMutationBlockedResponse | null => {
  if (!axios.isAxiosError(err)) {
    return null;
  }

  const data = err.response?.data;
  if (!data || typeof data !== 'object') {
    return null;
  }

  if (!('message' in data) || !('diagnostics' in data)) {
    return null;
  }

  return data as StockMutationBlockedResponse;
};

const getStockEditErrorMessage = (err: unknown): string => {
  if (!axios.isAxiosError(err)) {
    return 'Ошибка сохранения акции';
  }

  if (err.response == null) {
    return 'Не удалось отправить запрос. Проверьте сеть и повторите.';
  }

  const { status, data } = err.response;
  if (typeof data === 'string' && data.trim().length > 0) {
    return data;
  }

  if (data != null && typeof data === 'object') {
    if ('title' in data && typeof data.title === 'string' && data.title.trim().length > 0) {
      const problemTitle = data.title.trim();
      const errors =
        'errors' in data
        && data.errors != null
        && typeof data.errors === 'object'
          ? Object.entries(data.errors as Record<string, unknown>)
              .flatMap(([, value]) => (Array.isArray(value) ? value : []))
              .filter((value): value is string => typeof value === 'string' && value.trim().length > 0)
          : [];
      if (errors.length > 0) {
        return `${problemTitle}: ${errors.join(' ')}`;
      }
      return problemTitle;
    }

    if ('message' in data && typeof data.message === 'string' && data.message.trim().length > 0) {
      return data.message.trim();
    }
  }

  if (status === 404) {
    return 'Endpoint редактирования не найден (404). Проверьте актуальность backend deployment.';
  }
  if (status === 405) {
    return 'Метод редактирования не поддерживается (405). Проверьте маршрутизацию API.';
  }
  if (status >= 500) {
    return `Ошибка сервера (${status}). Повторите позже или проверьте backend-логи.`;
  }
  if (status === 400) {
    return 'Сервер отклонил изменения (400). Проверьте поля формы и повторите.';
  }

  return `Ошибка сохранения акции (${status})`;
};

const renderBlockersMessage = (response: StockMutationBlockedResponse): string => {
  const details = response.diagnostics.blockers
    .map((blocker) => {
      const names = blocker.relatedNames.length > 0 ? ` (${blocker.relatedNames.join(', ')})` : '';
      return `• ${blocker.displayName}: ${blocker.count}${names}`;
    })
    .join('\n');
  return details.length > 0 ? `${response.message}\n${details}` : response.message;
};

const getStockPermanentDeleteErrorMessage = (err: unknown): string => {
  const blocked = getStockMutationBlockedResponse(err);
  if (blocked) {
    return renderBlockersMessage(blocked);
  }

  if (axios.isAxiosError(err) && typeof err.response?.data === 'string' && err.response.data.trim().length > 0) {
    return err.response.data;
  }

  return STOCK_PERMANENT_DELETE_GENERIC_ERROR;
};

const renderBlockersList = (blockers: StockDependencyBlockerResponse[]) => (
  <ul style={{ margin: 0, paddingInlineStart: 18 }}>
    {blockers.map((blocker) => (
      <li key={`${blocker.category}-${blocker.displayName}`}>
        {`${blocker.displayName}: ${blocker.count}`}
        {blocker.relatedNames.length > 0 ? ` (${blocker.relatedNames.join(', ')})` : ''}
      </li>
    ))}
  </ul>
);

type StockDeleteActionProps = {
  isProtected: boolean;
  onDelete: () => void;
};

export const StockDeleteAction: React.FC<StockDeleteActionProps> = ({ isProtected, onDelete }) => {
  const buttonWithTooltip = (
    <Tooltip title={isProtected ? PROTECTED_STOCK_DELETE_TOOLTIP : STOCK_DELETE_TOOLTIP}>
      <span>
        <Button icon={<DeleteOutlined />} size="small" aria-label="Удалить из отслеживаемых" disabled={isProtected} />
      </span>
    </Tooltip>
  );

  if (isProtected) {
    return buttonWithTooltip;
  }

  return (
    <Popconfirm
      title="Удалить из отслеживаемых? Акция останется в «Список акций», индексах и портфелях."
      onConfirm={onDelete}
      okText="Да"
      cancelText="Нет"
    >
      {buttonWithTooltip}
    </Popconfirm>
  );
};

const TICKER_COL_WIDTH = 220;
const NAME_COL_WIDTH = 300;
const SAVED_PRICE_COL_WIDTH = 130;
const INDEX_MEMBERSHIP_COL_WIDTH = 220;
export const CHANGE_EUR_COL_WIDTH = 108;
export const CHANGE_PCT_COL_WIDTH = 75;
export const PRICE_TIME_COL_WIDTH = 135;
export const API_PRICE_COL_WIDTH = 130;
export const ACTIONS_COL_WIDTH = 180;
const TICKER_META_SPACE_WIDTH = 70;
const TICKER_TEXT_MAX_WIDTH = TICKER_COL_WIDTH - TICKER_META_SPACE_WIDTH;
const STOCKS_TABLE_SCROLL_X =
  TICKER_COL_WIDTH
  + NAME_COL_WIDTH
  + SAVED_PRICE_COL_WIDTH
  + CHANGE_EUR_COL_WIDTH
  + CHANGE_PCT_COL_WIDTH
  + PRICE_TIME_COL_WIDTH
  + API_PRICE_COL_WIDTH
  + ACTIONS_COL_WIDTH;
export const PRICE_TIME_FORMAT = 'DD.MM.YY HH:mm';
export const STOCKS_CHANGE_COMPACT_CLASS = 'stock-change-compact-col';
export const STOCKS_API_AREA_COMPACT_CLASS = 'stock-api-area-compact-col';
export const STOCKS_RIGHT_COMPACT_COLUMN_TITLES = ['Цена API', 'Время', 'Действия'] as const;
export const STOCKS_RIGHT_ALIGNED_MONEY_KEYS = ['savedPrice', 'changeEur', 'apiPrice'] as const;
const ELLIPSIS_STYLE: React.CSSProperties = { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' };
const CELL_BASE_STYLE: React.CSSProperties = { display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 };
const CELL_NOWRAP_STYLE: React.CSSProperties = { ...CELL_BASE_STYLE, whiteSpace: 'nowrap' };
const FLEX_MIN_WIDTH_STYLE: React.CSSProperties = { minWidth: 0, flex: 1 };
const TRACKING_STATUS_CATALOG_ONLY: StockTrackingStatus = 0;

type LivePriceEntry = {
  quote: StockQuoteResponse | null;
  loading: boolean;
};

type ChartRow = { _isChartRow: true; _stockId: number };
type TableRow = Stock | ChartRow;

const isChartRow = (record: TableRow): record is ChartRow => !!(record as ChartRow)._isChartRow;

const preserveEntry = (current: LivePriceEntry | undefined, loading: boolean): LivePriceEntry => ({
  quote: current?.quote ?? null,
  loading,
});

export const STOCKS_TABLE_TOTAL_COLS = 8;

/**
 * Catalog mode adds an Indices column that is absent in tracked mode (8 cols).
 * Tracked mode never showed Indices; catalog removes Статус and adds Indices — net +1 vs tracked baseline.
 */
const CATALOG_TOTAL_COLS = STOCKS_TABLE_TOTAL_COLS + 1;

/** Catalog mode with performance column adds one more column. */
const CATALOG_WITH_PERF_TOTAL_COLS = CATALOG_TOTAL_COLS + 1;

export const CATALOG_SORT_NAME_MODE = 'name' as const;
export type CatalogSortMode = typeof CATALOG_SORT_NAME_MODE | StockHistoryRange;
export const CATALOG_SORT_MODE_OPTIONS: Array<{ label: string; value: CatalogSortMode }> = [
  { label: 'По названию', value: CATALOG_SORT_NAME_MODE },
  ...STOCK_HISTORY_RANGE_OPTIONS,
];
export const isCatalogPeriodSortMode = (mode: CatalogSortMode): mode is StockHistoryRange =>
  mode !== CATALOG_SORT_NAME_MODE;

const TRACKED_STOCK_LIST_QUERY_PARAM = 'tq';
const TRACKED_STOCK_LIST_SORT_PARAM = 'tsort';
const TRACKED_STOCK_LIST_DIRECTION_PARAM = 'tdir';
const TRACKED_ADVANCED_FILTER_PARAM_NAMES = {
  exchangesParam: 'texchanges',
  sectorsParam: 'tsectors',
  industriesParam: 'tindustries',
} as const;

type StockListQueryState = {
  query: string;
  sortMode: CatalogSortMode;
  sortDirection: 'asc' | 'desc';
  page: number;
  advancedFilters: AdvancedStockFilters;
};

type StockListUrlKeys = {
  queryParam: string;
  sortParam: string;
  directionParam: string;
  pageParam?: string;
  advancedFilterParamNames?: {
    exchangesParam: string;
    sectorsParam: string;
    industriesParam: string;
  };
};

const TRACKED_URL_KEYS: StockListUrlKeys = {
  queryParam: TRACKED_STOCK_LIST_QUERY_PARAM,
  sortParam: TRACKED_STOCK_LIST_SORT_PARAM,
  directionParam: TRACKED_STOCK_LIST_DIRECTION_PARAM,
  advancedFilterParamNames: TRACKED_ADVANCED_FILTER_PARAM_NAMES,
};

const STOCK_SORT_MODE_VALUES = new Set<CatalogSortMode>(CATALOG_SORT_MODE_OPTIONS.map((option) => option.value));

const parseSortModeFromUrl = (value: string | null): CatalogSortMode =>
  (value != null && STOCK_SORT_MODE_VALUES.has(value as CatalogSortMode))
    ? (value as CatalogSortMode)
    : CATALOG_SORT_NAME_MODE;

const parseSortDirectionFromUrl = (value: string | null): 'asc' | 'desc' => (value === 'asc' ? 'asc' : 'desc');

const parsePageFromUrl = (value: string | null): number => {
  const num = Number(value);
  return Number.isInteger(num) && num > 0 ? num : 1;
};

const parseStockListQueryState = (
  params: URLSearchParams,
  keys: StockListUrlKeys,
): StockListQueryState => ({
  query: params.get(keys.queryParam)?.trim() ?? '',
  sortMode: parseSortModeFromUrl(params.get(keys.sortParam)),
  sortDirection: parseSortDirectionFromUrl(params.get(keys.directionParam)),
  page: keys.pageParam ? parsePageFromUrl(params.get(keys.pageParam)) : 1,
  advancedFilters: parseAdvancedStockFiltersFromSearchParams(params, keys.advancedFilterParamNames),
});

const serializeStockListQueryState = (
  current: URLSearchParams,
  state: StockListQueryState,
  keys: StockListUrlKeys,
): URLSearchParams => {
  const next = serializeAdvancedStockFiltersToSearchParams(current, state.advancedFilters, keys.advancedFilterParamNames);
  const queryValue = state.query.trim();

  next.delete(keys.queryParam);
  next.delete(keys.sortParam);
  next.delete(keys.directionParam);
  if (keys.pageParam) {
    next.delete(keys.pageParam);
  }

  if (queryValue.length > 0) {
    next.set(keys.queryParam, queryValue);
  }

  if (state.sortMode !== CATALOG_SORT_NAME_MODE) {
    next.set(keys.sortParam, state.sortMode);
  }

  if (state.sortDirection !== 'desc') {
    next.set(keys.directionParam, state.sortDirection);
  }

  if (keys.pageParam && state.page > 1) {
    next.set(keys.pageParam, String(state.page));
  }

  return next;
};

const PERFORMANCE_COL_WIDTH = 110;

/** Label shown on the delayed-quote badge. */
export const STALE_DELAY_LABEL = 'Задержано';

export const getApiPriceCurrency = (quote: StockQuoteResponse | null | undefined): string | null =>
  quote?.currency ?? quote?.normalizedQuoteCurrency ?? null;

export const getApiPriceText = (live: LivePriceEntry | null | undefined): string => {
  if (live?.loading) return '...';
  const quote = live?.quote;
  const currency = getApiPriceCurrency(quote);
  if (!quote || !currency) return '—';
  return fmtCur(quote.rawCurrentPrice, currency);
};

export const getApiPriceTooltip = (quote: StockQuoteResponse | null | undefined): string | undefined =>
  quote && quote.quoteUnitMultiplier !== 1 && quote.normalizedQuoteCurrency
    ? `Нормализовано: ${quote.normalizedCurrentPrice.toFixed(3)} ${quote.normalizedQuoteCurrency}`
    : undefined;

/**
 * Maps provider market state to a UI status.
 * Returns 'open' for REGULAR, 'closed' for any other known state,
 * and null when there is no live quote (loading or absent).
 */
export const getMarketStatus = (live: LivePriceEntry | null | undefined): 'open' | 'closed' | null => {
  if (!live || live.loading) return null;
  if (!live.quote) return null;
  return live.quote.marketState === 'REGULAR' ? 'open' : 'closed';
};

type StockRowActionsProps = {
  stock: Stock;
  live: LivePriceEntry | undefined;
  isProtectedStock: boolean;
  onRefresh: (stock: Stock) => void;
  onOpenFundamentals: (stock: Stock) => void;
  onOpenEdit: (stock: Stock) => void;
  onDelete: (stockId: number) => void;
  trackingAction?: React.ReactElement;
};

export const renderStockRowActions = ({
  stock,
  live,
  isProtectedStock,
  onRefresh,
  onOpenFundamentals,
  onOpenEdit,
  onDelete,
  trackingAction,
}: StockRowActionsProps): React.ReactElement => {
  const quote = live?.quote ?? null;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
      {quote?.conversionWarning && !live?.loading && (
        <Tag color="gold" style={{ fontSize: 16, lineHeight: '22px', padding: '0 4px' }}>Нет EUR</Tag>
      )}
      <Button
        icon={<ReloadOutlined />}
        size="small"
        loading={live?.loading}
        disabled={!stock.ticker?.trim()}
        onClick={() => onRefresh(stock)}
      />
      <Tooltip title="Фундаментальные данные">
        <Button
          icon={<FundOutlined />}
          size="small"
          aria-label="Фундаментальные данные"
          onClick={() => onOpenFundamentals(stock)}
        />
      </Tooltip>
      <Tooltip title="Изменить">
        <Button
          icon={<EditOutlined />}
          size="small"
          aria-label="Изменить"
          onClick={() => onOpenEdit(stock)}
        />
      </Tooltip>
      {trackingAction ?? <StockDeleteAction isProtected={isProtectedStock} onDelete={() => onDelete(stock.id)} />}
    </div>
  );
};


type StocksPageMode = 'tracked' | 'catalog';

interface StocksPageProps {
  mode?: StocksPageMode;
}

const StocksPage: React.FC<StocksPageProps> = ({ mode = 'tracked' }) => {
  const isCatalogMode = mode === 'catalog';
  const [stocks, setStocks] = useState<Stock[]>([]);
  const [sectors, setSectors] = useState<SectorDto[]>([]);
  const [marketIndices, setMarketIndices] = useState<MarketIndex[]>([]);
  const [portfolios, setPortfolios] = useState<Portfolio[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [editingStock, setEditingStock] = useState<Stock | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const submitInFlightRef = useRef(false);
  const [editInlineError, setEditInlineError] = useState<string | null>(null);
  const [editInlineBlockers, setEditInlineBlockers] = useState<StockDependencyBlockerResponse[]>([]);
  const [permanentDeleteTarget, setPermanentDeleteTarget] = useState<Stock | null>(null);
  const [permanentDeleteSubmitting, setPermanentDeleteSubmitting] = useState(false);
  const [permanentDeleteInlineError, setPermanentDeleteInlineError] = useState<string | null>(null);
  const [permanentDeleteInlineBlockers, setPermanentDeleteInlineBlockers] = useState<StockDependencyBlockerResponse[]>([]);
  const [refreshing, setRefreshing] = useState(false);
  const [livePrices, setLivePrices] = useState<Record<number, LivePriceEntry>>({});
  const [expandedStockId, setExpandedStockId] = useState<number | null>(null);
  const [catalogPage, setCatalogPage] = useState(1);
  const [fundamentalsStock, setFundamentalsStock] = useState<Stock | null>(null);
  const [countdown, setCountdown] = useState(AUTO_REFRESH_INTERVAL);
  const [trackingLoadingByStock, setTrackingLoadingByStock] = useState<Record<number, boolean>>({});
  const [catalogQuery, setCatalogQuery] = useState('');
  const [catalogSortMode, setCatalogSortMode] = useState<CatalogSortMode>(CATALOG_SORT_NAME_MODE);
  const [catalogSortDirection, setCatalogSortDirection] = useState<'asc' | 'desc'>('desc');
  const [trackedQuery, setTrackedQuery] = useState('');
  const [trackedSortMode, setTrackedSortMode] = useState<CatalogSortMode>(CATALOG_SORT_NAME_MODE);
  const [trackedSortDirection, setTrackedSortDirection] = useState<'asc' | 'desc'>('desc');
  const [catalogAdvancedFilters, setCatalogAdvancedFilters] = useState<AdvancedStockFilters>(EMPTY_ADVANCED_STOCK_FILTERS);
  const [catalogAdvancedDraft, setCatalogAdvancedDraft] = useState<AdvancedStockFilters>(EMPTY_ADVANCED_STOCK_FILTERS);
  const [catalogFiltersOpen, setCatalogFiltersOpen] = useState(false);
  const [trackedAdvancedFilters, setTrackedAdvancedFilters] = useState<AdvancedStockFilters>(EMPTY_ADVANCED_STOCK_FILTERS);
  const [trackedAdvancedDraft, setTrackedAdvancedDraft] = useState<AdvancedStockFilters>(EMPTY_ADVANCED_STOCK_FILTERS);
  const [trackedFiltersOpen, setTrackedFiltersOpen] = useState(false);
  const [catalogUrlSearch, setCatalogUrlSearch] = useState(() => window.location.search);
  const [trackedUrlSearch, setTrackedUrlSearch] = useState(() => window.location.search);
  const [performanceMap, setPerformanceMap] = useState<PerformanceMap>(new Map());
  const [performanceLoading, setPerformanceLoading] = useState(false);
  const [performanceError, setPerformanceError] = useState<string | null>(null);
  const { user, logout } = useAuth();
  const stocksRef = useRef<Stock[]>([]);
  const performanceAbortRef = useRef<AbortController | null>(null);
  const performanceRequestIdRef = useRef(0);
  const portfolioStockIds = useMemo(() => {
    const ids = new Set<number>();
    portfolios.forEach((portfolio) => {
      portfolio.items?.forEach((item) => {
        if ((item.stockId ?? 0) > 0) {
          ids.add(item.stockId);
        }
      });
    });
    return ids;
  }, [portfolios]);
  const marketIndexNameById = useMemo(() => new Map<number, string>(marketIndices.map((idx) => [idx.id, idx.name])), [marketIndices]);
  const industryToSectorMap = useMemo(() => buildIndustryToSectorMap(sectors), [sectors]);
  const sectorFilterOptions = useMemo(() => getActiveSectorOptions(sectors), [sectors]);
  const selectedAdvancedDraft = isCatalogMode ? catalogAdvancedDraft : trackedAdvancedDraft;
  const selectedAdvancedFilters = isCatalogMode ? catalogAdvancedFilters : trackedAdvancedFilters;
  const selectedQuery = isCatalogMode ? catalogQuery : trackedQuery;
  const selectedSortMode = isCatalogMode ? catalogSortMode : trackedSortMode;
  const selectedSortDirection = isCatalogMode ? catalogSortDirection : trackedSortDirection;
  const industryFilterOptions = useMemo(
    () => getIndustryOptionsForSelectedSectors(sectors, selectedAdvancedDraft.sectorIds),
    [selectedAdvancedDraft.sectorIds, sectors],
  );
  const activeAdvancedFilterGroups = useMemo(
    () => countActiveAdvancedFilterGroups(selectedAdvancedFilters),
    [selectedAdvancedFilters],
  );
  const filteredStocks = useMemo(() => {
    const query = selectedQuery.trim().toLowerCase();
    const base = query.length === 0
      ? stocks
      : stocks.filter((stock) => {
          const indexNames = (stock.marketIndexIds ?? [])
            .map((id) => marketIndexNameById.get(id) ?? '')
            .join(' ')
            .toLowerCase();
          return stock.ticker.toLowerCase().includes(query)
            || stock.name.toLowerCase().includes(query)
            || stock.commonName.toLowerCase().includes(query)
            || stock.exchange.toLowerCase().includes(query)
            || indexNames.includes(query);
        });

    const filteredByAdvanced = base.filter((stock) =>
      matchesStockAdvancedFilters(stock, selectedAdvancedFilters, industryToSectorMap));

    if (isCatalogPeriodSortMode(selectedSortMode)) {
      const dir = selectedSortDirection === 'asc' ? 1 : -1;
      return [...filteredByAdvanced].sort((a, b) => {
        const pa = performanceMap.get(a.id) ?? null;
        const pb = performanceMap.get(b.id) ?? null;
        const aHas = pa != null;
        const bHas = pb != null;

        if (aHas && bHas) {
          if (pa !== pb) return dir * (pa - pb);
        } else if (aHas) {
          return -1;
        } else if (bHas) {
          return 1;
        }

        return a.id - b.id;
      });
    }

    return [...filteredByAdvanced].sort((a, b) => {
      const nameA = (a.commonName || a.name || '').trim();
      const nameB = (b.commonName || b.name || '').trim();
      const cmp = nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      return cmp !== 0 ? cmp : a.ticker.localeCompare(b.ticker, undefined, { sensitivity: 'base' });
    });
  }, [
    industryToSectorMap,
    isCatalogMode,
    performanceMap,
    marketIndexNameById,
    selectedAdvancedFilters,
    selectedQuery,
    selectedSortDirection,
    selectedSortMode,
    stocks,
  ]);
  const { portfolioGroup, fraGroup, nyseGroup } = useMemo(() => {
    const next = {
      portfolioGroup: [] as Stock[],
      fraGroup: [] as Stock[],
      nyseGroup: [] as Stock[],
    };

    for (const stock of filteredStocks) {
      if (portfolioStockIds.has(stock.id)) {
        next.portfolioGroup.push(stock);
      } else if (stock.exchange === 'Frankfurt') {
        next.fraGroup.push(stock);
      } else {
        next.nyseGroup.push(stock);
      }
    }

    return next;
  }, [filteredStocks, portfolioStockIds]);
  const selectedSnapshotByStockId = useMemo(() => {
    const map = new Map<number, ReturnType<typeof resolveNewestCurrentPriceSnapshot>>();
    for (const stock of stocks) {
      map.set(stock.id, resolveNewestCurrentPriceSnapshot(stock, livePrices[stock.id]?.quote ?? null));
    }
    return map;
  }, [livePrices, stocks]);
  const getSelectedSnapshot = useCallback((stock: Stock) => {
    const fromMap = selectedSnapshotByStockId.get(stock.id);
    if (fromMap) return fromMap;
    return resolveNewestCurrentPriceSnapshot(stock, livePrices[stock.id]?.quote ?? null);
  }, [livePrices, selectedSnapshotByStockId]);

  useEffect(() => {
    if (!isCatalogMode) {
      return;
    }

    const maxPage = Math.max(1, Math.ceil(filteredStocks.length / CATALOG_PAGE_SIZE));
    setCatalogPage((prev) => Math.min(prev, maxPage));
  }, [filteredStocks.length, isCatalogMode]);

  const clearPerformanceStateAndAbort = useCallback(() => {
    performanceRequestIdRef.current += 1;
    performanceAbortRef.current?.abort();
    performanceAbortRef.current = null;
    setPerformanceLoading(false);
    setPerformanceError(null);
    setPerformanceMap(new Map());
  }, []);

  const loadPerformance = useCallback(async (range: StockHistoryRange) => {
    performanceAbortRef.current?.abort();
    const ctrl = new AbortController();
    const requestId = ++performanceRequestIdRef.current;
    performanceAbortRef.current = ctrl;
    setPerformanceLoading(true);
    setPerformanceMap(new Map());
    setPerformanceError(null);
    try {
      const res = await getStockCatalogPerformance(range, ctrl.signal);
      if (ctrl.signal.aborted) return;
      if (performanceRequestIdRef.current !== requestId) return;
      const map = new Map<number, number | null>(
        res.data.items.map((item) => [item.stockId, item.changePercent ?? null]),
      );
      setPerformanceMap(map);
    } catch (err) {
      if (ctrl.signal.aborted) return;
      if (performanceRequestIdRef.current !== requestId) return;
      setPerformanceError('Не удалось загрузить данные о росте');
    } finally {
      if (!ctrl.signal.aborted && performanceRequestIdRef.current === requestId) {
        setPerformanceLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    if (!isCatalogPeriodSortMode(selectedSortMode)) {
      clearPerformanceStateAndAbort();
      return;
    }
    void loadPerformance(selectedSortMode);
  }, [clearPerformanceStateAndAbort, loadPerformance, selectedSortMode]);

  useEffect(() => {
    const onPopState = () => {
      const nextSearch = window.location.search;
      setCatalogUrlSearch(nextSearch);
      setTrackedUrlSearch(nextSearch);
    };
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, []);

  useEffect(() => {
    if (!isCatalogMode) {
      return;
    }

    const parsed = parseAdvancedStockFiltersFromSearchParams(new URLSearchParams(catalogUrlSearch));
    const normalized = normalizeAdvancedStockFiltersWithDirectory(parsed, sectors);
    if (!areAdvancedStockFiltersEqual(catalogAdvancedFilters, normalized)) {
      setCatalogAdvancedFilters(normalized);
    }

    const canonicalParams = serializeAdvancedStockFiltersToSearchParams(
      new URLSearchParams(catalogUrlSearch),
      normalized,
    );
    const canonicalSearch = canonicalParams.toString();
    const currentSearch = new URLSearchParams(catalogUrlSearch).toString();

    if (canonicalSearch !== currentSearch) {
      const nextUrl = `${window.location.pathname}${canonicalSearch ? `?${canonicalSearch}` : ''}${window.location.hash}`;
      window.history.replaceState(window.history.state, '', nextUrl);
      setCatalogUrlSearch(window.location.search);
    }
  }, [catalogAdvancedFilters, catalogUrlSearch, isCatalogMode, sectors]);

  useEffect(() => {
    if (isCatalogMode) {
      return;
    }

    const parsed = parseStockListQueryState(new URLSearchParams(trackedUrlSearch), TRACKED_URL_KEYS);
    const normalizedFilters = normalizeAdvancedStockFiltersWithDirectory(parsed.advancedFilters, sectors);
    const normalizedState: StockListQueryState = {
      ...parsed,
      advancedFilters: normalizedFilters,
      page: 1,
    };

    if (trackedQuery !== normalizedState.query) {
      setTrackedQuery(normalizedState.query);
    }
    if (trackedSortMode !== normalizedState.sortMode) {
      setTrackedSortMode(normalizedState.sortMode);
    }
    if (trackedSortDirection !== normalizedState.sortDirection) {
      setTrackedSortDirection(normalizedState.sortDirection);
    }
    if (!areAdvancedStockFiltersEqual(trackedAdvancedFilters, normalizedFilters)) {
      setTrackedAdvancedFilters(normalizedFilters);
    }

    const canonicalParams = serializeStockListQueryState(
      new URLSearchParams(trackedUrlSearch),
      normalizedState,
      TRACKED_URL_KEYS,
    );
    const canonicalSearch = canonicalParams.toString();
    const currentSearch = new URLSearchParams(trackedUrlSearch).toString();
    if (canonicalSearch !== currentSearch) {
      const nextUrl = `${window.location.pathname}${canonicalSearch ? `?${canonicalSearch}` : ''}${window.location.hash}`;
      window.history.replaceState(window.history.state, '', nextUrl);
      setTrackedUrlSearch(window.location.search);
    }
  }, [
    isCatalogMode,
    sectors,
    trackedAdvancedFilters,
    trackedQuery,
    trackedSortDirection,
    trackedSortMode,
    trackedUrlSearch,
  ]);

  const commitCatalogFiltersToUrl = useCallback((nextFilters: AdvancedStockFilters, mode: 'push' | 'replace') => {
    const base = new URLSearchParams(window.location.search);
    const nextParams = serializeAdvancedStockFiltersToSearchParams(base, nextFilters);
    const nextSearch = nextParams.toString();
    const currentSearch = base.toString();
    const nextUrl = `${window.location.pathname}${nextSearch ? `?${nextSearch}` : ''}${window.location.hash}`;

    if (mode === 'push') {
      if (currentSearch !== nextSearch) {
        window.history.pushState(window.history.state, '', nextUrl);
      }
    } else if (currentSearch !== nextSearch) {
      window.history.replaceState(window.history.state, '', nextUrl);
    }

    setCatalogUrlSearch(window.location.search);
  }, []);

  const commitTrackedQueryStateToUrl = useCallback((nextState: StockListQueryState, mode: 'push' | 'replace') => {
    const base = new URLSearchParams(window.location.search);
    const nextParams = serializeStockListQueryState(base, nextState, TRACKED_URL_KEYS);
    const nextSearch = nextParams.toString();
    const currentSearch = base.toString();
    const nextUrl = `${window.location.pathname}${nextSearch ? `?${nextSearch}` : ''}${window.location.hash}`;

    if (mode === 'push') {
      if (currentSearch !== nextSearch) {
        window.history.pushState(window.history.state, '', nextUrl);
      }
    } else if (currentSearch !== nextSearch) {
      window.history.replaceState(window.history.state, '', nextUrl);
    }

    setTrackedUrlSearch(window.location.search);
  }, []);

  const handleCatalogQueryChange = useCallback((value: string) => {
    setCatalogQuery(value);
  }, []);

  const handleTrackedQueryChange = useCallback((value: string) => {
    setTrackedQuery(value);
    commitTrackedQueryStateToUrl({
      query: value,
      sortMode: trackedSortMode,
      sortDirection: trackedSortDirection,
      page: 1,
      advancedFilters: trackedAdvancedFilters,
    }, 'replace');
  }, [commitTrackedQueryStateToUrl, trackedAdvancedFilters, trackedSortDirection, trackedSortMode]);

  const handleCatalogSortModeChange = useCallback((mode: CatalogSortMode) => {
    setCatalogSortMode(mode);
    setCatalogPage(1);
  }, []);

  const handleTrackedSortModeChange = useCallback((mode: CatalogSortMode) => {
    setTrackedSortMode(mode);
    commitTrackedQueryStateToUrl({
      query: trackedQuery,
      sortMode: mode,
      sortDirection: trackedSortDirection,
      page: 1,
      advancedFilters: trackedAdvancedFilters,
    }, 'push');
  }, [commitTrackedQueryStateToUrl, trackedAdvancedFilters, trackedQuery, trackedSortDirection]);

  const handleCatalogSortDirectionToggle = useCallback(() => {
    setCatalogSortDirection((prev) => (prev === 'desc' ? 'asc' : 'desc'));
  }, []);

  const handleTrackedSortDirectionToggle = useCallback(() => {
    const nextDirection = trackedSortDirection === 'desc' ? 'asc' : 'desc';
    setTrackedSortDirection(nextDirection);
    commitTrackedQueryStateToUrl({
      query: trackedQuery,
      sortMode: trackedSortMode,
      sortDirection: nextDirection,
      page: 1,
      advancedFilters: trackedAdvancedFilters,
    }, 'push');
  }, [commitTrackedQueryStateToUrl, trackedAdvancedFilters, trackedQuery, trackedSortDirection, trackedSortMode]);

  const handleCatalogPageChange = useCallback((page: number) => {
    setCatalogPage(page);
  }, []);

  useEffect(() => {
    if (!catalogFiltersOpen) {
      return;
    }

    setCatalogAdvancedDraft((prev) => {
      const normalized = normalizeAdvancedStockFilters(prev);
      const nextIndustries = pruneInvalidIndustrySelections(normalized.industryIds, normalized.sectorIds, sectors);
      if (nextIndustries.length === normalized.industryIds.length && nextIndustries.every((id, idx) => id === normalized.industryIds[idx])) {
        return normalized;
      }

      return { ...normalized, industryIds: nextIndustries };
    });
  }, [catalogFiltersOpen, sectors]);

  const handleCatalogAdvancedDraftChange = useCallback((next: AdvancedStockFilters) => {
    const normalized = normalizeAdvancedStockFilters(next);
    const nextIndustryIds = pruneInvalidIndustrySelections(normalized.industryIds, normalized.sectorIds, sectors);
    setCatalogAdvancedDraft({ ...normalized, industryIds: nextIndustryIds });
  }, [sectors]);

  const handleTrackedAdvancedDraftChange = useCallback((next: AdvancedStockFilters) => {
    const normalized = normalizeAdvancedStockFilters(next);
    const nextIndustryIds = pruneInvalidIndustrySelections(normalized.industryIds, normalized.sectorIds, sectors);
    setTrackedAdvancedDraft({ ...normalized, industryIds: nextIndustryIds });
  }, [sectors]);

  const openCatalogAdvancedFilters = useCallback(() => {
    setCatalogAdvancedDraft(catalogAdvancedFilters);
    setCatalogFiltersOpen(true);
  }, [catalogAdvancedFilters]);

  const closeCatalogAdvancedFilters = useCallback(() => {
    setCatalogFiltersOpen(false);
  }, []);

  const openTrackedAdvancedFilters = useCallback(() => {
    setTrackedAdvancedDraft(trackedAdvancedFilters);
    setTrackedFiltersOpen(true);
  }, [trackedAdvancedFilters]);

  const closeTrackedAdvancedFilters = useCallback(() => {
    setTrackedFiltersOpen(false);
  }, []);

  const applyCatalogAdvancedFilters = useCallback(() => {
    const normalized = normalizeAdvancedStockFiltersWithDirectory(catalogAdvancedDraft, sectors);
    setCatalogAdvancedFilters(normalized);
    setCatalogPage(1);
    commitCatalogFiltersToUrl(normalized, 'push');
    setCatalogFiltersOpen(false);
  }, [catalogAdvancedDraft, commitCatalogFiltersToUrl, sectors]);

  const applyTrackedAdvancedFilters = useCallback(() => {
    const normalized = normalizeAdvancedStockFiltersWithDirectory(trackedAdvancedDraft, sectors);
    setTrackedAdvancedFilters(normalized);
    commitTrackedQueryStateToUrl({
      query: trackedQuery,
      sortMode: trackedSortMode,
      sortDirection: trackedSortDirection,
      page: 1,
      advancedFilters: normalized,
    }, 'push');
    setTrackedFiltersOpen(false);
  }, [
    commitTrackedQueryStateToUrl,
    sectors,
    trackedAdvancedDraft,
    trackedQuery,
    trackedSortDirection,
    trackedSortMode,
  ]);

  const clearCatalogAdvancedDraft = useCallback(() => {
    setCatalogAdvancedDraft(EMPTY_ADVANCED_STOCK_FILTERS);
  }, []);

  const clearTrackedAdvancedDraft = useCallback(() => {
    setTrackedAdvancedDraft(EMPTY_ADVANCED_STOCK_FILTERS);
  }, []);

  const resetCatalogAdvancedFilters = useCallback(() => {
    if (countActiveAdvancedFilterGroups(catalogAdvancedFilters) === 0) {
      return;
    }

    setCatalogAdvancedFilters(EMPTY_ADVANCED_STOCK_FILTERS);
    setCatalogPage(1);
    commitCatalogFiltersToUrl(EMPTY_ADVANCED_STOCK_FILTERS, 'push');
  }, [catalogAdvancedFilters, commitCatalogFiltersToUrl]);

  useEffect(() => {
    if (!trackedFiltersOpen) {
      return;
    }

    setTrackedAdvancedDraft((prev) => {
      const normalized = normalizeAdvancedStockFilters(prev);
      const nextIndustries = pruneInvalidIndustrySelections(normalized.industryIds, normalized.sectorIds, sectors);
      if (nextIndustries.length === normalized.industryIds.length && nextIndustries.every((id, idx) => id === normalized.industryIds[idx])) {
        return normalized;
      }

      return { ...normalized, industryIds: nextIndustries };
    });
  }, [sectors, trackedFiltersOpen]);

  const resetTrackedAdvancedFilters = useCallback(() => {
    if (countActiveAdvancedFilterGroups(trackedAdvancedFilters) === 0) {
      return;
    }

    setTrackedAdvancedFilters(EMPTY_ADVANCED_STOCK_FILTERS);
    commitTrackedQueryStateToUrl({
      query: trackedQuery,
      sortMode: trackedSortMode,
      sortDirection: trackedSortDirection,
      page: 1,
      advancedFilters: EMPTY_ADVANCED_STOCK_FILTERS,
    }, 'push');
  }, [
    commitTrackedQueryStateToUrl,
    trackedAdvancedFilters,
    trackedQuery,
    trackedSortDirection,
    trackedSortMode,
  ]);

  useEffect(() => {
    return () => { performanceAbortRef.current?.abort(); };
  }, []);

  const fetchData = useCallback(async () => {
    setLoading(true);
    try {
      const stocksRequest = isCatalogMode ? getStockCatalog() : getTrackedStocks();
      const [stocksRes, portfoliosRes, lookupData] = await Promise.all([
        stocksRequest,
        getPortfolios(),
        loadStockMetadataLookups(),
      ]);
      setStocks(stocksRes.data);
      stocksRef.current = stocksRes.data;
      const persistedByStockId = new Map(stocksRes.data.map((stock) => [stock.id, stock]));
      setLivePrices((prev) => {
        const next: Record<number, LivePriceEntry> = {};
        for (const [stockIdText, entry] of Object.entries(prev)) {
          if (entry.loading || !entry.quote) {
            next[Number(stockIdText)] = entry;
            continue;
          }

          const persisted = persistedByStockId.get(Number(stockIdText));
          if (!persisted) {
            continue;
          }

          if (shouldKeepLiveOverlayAfterPersistedRefresh(persisted, entry.quote)) {
            next[Number(stockIdText)] = entry;
          }
        }

        return next;
      });
      setPortfolios(portfoliosRes.data);
      setSectors(lookupData.sectors);
      setMarketIndices(lookupData.marketIndices);
      if (lookupData.marketIndicesLoadFailed) {
        message.warning('Не удалось загрузить мировые индексы');
      }
    } catch {
      message.error('Ошибка загрузки данных');
    } finally {
      setLoading(false);
    }
  }, [isCatalogMode]);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  const persistConvertedPrice = useCallback(async (stock: Stock, quote: StockQuoteResponse) => {
    const patch = buildQuotePatch(quote);
    if (patch == null) {
      return null;
    }

    return (await updateStockQuote(stock.id, patch satisfies UpdateStockQuoteRequest)).data;
  }, []);

  const handleRefreshPrices = useCallback(async (silent = false) => {
    if (refreshing) return;
    setRefreshing(true);
    try {
      const currentStocks = stocksRef.current;
      const stocksWithTicker = currentStocks.filter((s) => {
        if (!s.ticker?.trim()) {
          return false;
        }
        if (!isCatalogMode) {
          return true;
        }
        return s.trackingStatus !== TRACKING_STATUS_CATALOG_ONLY;
      });

      setLivePrices((prev) => {
        const next = { ...prev };
        stocksWithTicker.forEach((stock) => {
          next[stock.id] = preserveEntry(prev[stock.id], true);
        });
        return next;
      });

      const results = await Promise.allSettled(
        stocksWithTicker.map(async (stock) => {
          try {
            const priceRes = await getStockPrice(stock.ticker, stock.exchange, stock.finanzenNetSlug);
            const quote = priceRes.data;

            setLivePrices((prev) => ({
              ...prev,
              [stock.id]: { quote, loading: false },
            }));

            await persistConvertedPrice(stock, quote);
            return { delayed: isQuoteDelayed(quote) };
          } catch (error) {
            setLivePrices((prev) => ({
              ...prev,
              [stock.id]: preserveEntry(prev[stock.id], false),
            }));
            throw error;
          }
        })
      );
      const failed = results.filter((r) => r.status === 'rejected').length;
      const delayed = results.filter((r) => r.status === 'fulfilled' && r.value.delayed).length;
      await fetchData();
      if (!silent) {
        if (failed === 0 && delayed === 0) {
          message.success('Цены обновлены');
        } else if (delayed > 0 && failed === 0) {
          message.warning(`Задержано: ${delayed}. Остальные цены обновлены`);
        } else if (failed > 0 && delayed === 0) {
          message.warning(`Цены обновлены частично (${failed} ошибок)`);
        } else {
          message.warning(`Цены обновлены частично (${failed} ошибок, ${delayed} задержано)`);
        }
      } else if (delayed > 0 && failed === 0) {
        message.info(`Авт. обновление: ${delayed} задержано`);
      } else if (failed > 0 && delayed === 0) {
        message.info(`Авт. обновление: ${failed} ошибок`);
      } else if (delayed > 0 || failed > 0) {
        message.info(`Авт. обновление: ${failed} ошибок, ${delayed} задержано`);
      } else {
        message.info('Цены автоматически обновлены');
      }
    } catch {
      if (!silent) message.error('Ошибка обновления цен');
    } finally {
      setRefreshing(false);
    }
  }, [isCatalogMode, persistConvertedPrice, refreshing]);

  useEffect(() => {
    if (isCatalogMode) return;
    const autoRefreshTimer = setInterval(() => {
      handleRefreshPrices(true);
      setCountdown(AUTO_REFRESH_INTERVAL);
    }, AUTO_REFRESH_INTERVAL * 1000);
    return () => clearInterval(autoRefreshTimer);
  }, [handleRefreshPrices, isCatalogMode]);

  useEffect(() => {
    if (isCatalogMode) return;
    setCountdown(AUTO_REFRESH_INTERVAL);
    const countdownTimer = setInterval(() => {
      setCountdown((prev) => (prev <= 1 ? AUTO_REFRESH_INTERVAL : prev - 1));
    }, 1000);
    return () => clearInterval(countdownTimer);
  }, [isCatalogMode]);

  const formatCountdown = (seconds: number) => {
    const m = Math.floor(seconds / 60);
    const s = seconds % 60;
    return `${m}:${s.toString().padStart(2, '0')}`;
  };

  const openCreateModal = () => {
    setEditingStock(null);
    setEditInlineError(null);
    setEditInlineBlockers([]);
    setModalOpen(true);
  };

  const openEditModal = (stock: Stock) => {
    setEditingStock(stock);
    setEditInlineError(null);
    setEditInlineBlockers([]);
    setModalOpen(true);
  };

  const confirmIdentityTransitionAsync = (transitionLabel: string) => new Promise<boolean>((resolve) => {
    Modal.confirm({
      title: 'Подтвердите изменение тикера / биржи',
      content: (
        <div style={{ display: 'grid', gap: 8 }}>
          <Typography.Text strong>{transitionLabel}</Typography.Text>
          <Typography.Text type="secondary">
            Будут очищены история, снапшоты котировок и provider-данные. После сохранения потребуется повторная загрузка.
          </Typography.Text>
        </div>
      ),
      okText: 'Подтвердить изменение',
      cancelText: 'Отмена',
      onOk: () => resolve(true),
      onCancel: () => resolve(false),
    });
  });

  const handleSubmit = async (
    values: Parameters<typeof buildUpdateStockMetadataPayload>[0],
    context: { identityEditingEnabled: boolean },
  ) => {
    if (submitInFlightRef.current) {
      return;
    }

    submitInFlightRef.current = true;
    setEditInlineError(null);
    setEditInlineBlockers([]);
    setSubmitting(true);
    try {
      if (editingStock) {
        const newTicker = values.ticker.trim().toUpperCase();
        const newExchange = values.exchange;
        const oldTicker = editingStock.ticker;
        const oldExchange = editingStock.exchange;
        const identityChanged = newTicker !== oldTicker || newExchange !== oldExchange;
        if (identityChanged) {
          if (!context.identityEditingEnabled) {
            setEditInlineError('Сначала включите режим «Изменить тикер / биржу».');
            return;
          }

          const transitionLabel = `${oldTicker} (${oldExchange}) → ${newTicker} (${newExchange})`;
          const confirmed = await confirmIdentityTransitionAsync(transitionLabel);
          if (!confirmed) {
            return;
          }
          await updateStockEdit(editingStock.id, {
            ...buildUpdateStockMetadataPayload(values),
            ticker: newTicker,
            exchange: newExchange,
            confirmationText: transitionLabel,
            identityEditingEnabled: true,
            retainProviderSymbol: false,
          });
        } else {
          await updateStockEdit(editingStock.id, {
            ...buildUpdateStockMetadataPayload(values),
            ticker: oldTicker,
            exchange: oldExchange,
            confirmationText: '',
            identityEditingEnabled: false,
            retainProviderSymbol: false,
          });
        }

        await fetchData();
        message.success('Акция обновлена');
      } else {
        await createStock(buildCreateStockPayload(values));
        await fetchData();
        message.success('Акция добавлена');
      }
      setModalOpen(false);
      setEditingStock(null);
      setEditInlineError(null);
      setEditInlineBlockers([]);
    } catch (err: unknown) {
      const blocked = getStockMutationBlockedResponse(err);
      if (blocked) {
        setEditInlineError(blocked.message);
        setEditInlineBlockers(blocked.diagnostics.blockers);
        return;
      }
      setEditInlineError(getStockEditErrorMessage(err));
    } finally {
      setSubmitting(false);
      submitInFlightRef.current = false;
    }
  };

  const handleDelete = async (id: number) => {
    try {
      await untrackStock(id);
      message.success('Акция удалена из отслеживаемых');
      fetchData();
    } catch (err: unknown) {
      message.error(getStockDeleteErrorMessage(err));
    }
  };

  const showPermanentDeleteDialog = (stock: Stock) => {
    setPermanentDeleteTarget(stock);
    setPermanentDeleteSubmitting(false);
    setPermanentDeleteInlineError(null);
    setPermanentDeleteInlineBlockers([]);
  };

  const closePermanentDeleteDialog = (force = false) => {
    if (permanentDeleteSubmitting && !force) {
      return;
    }

    setPermanentDeleteTarget(null);
    setPermanentDeleteInlineError(null);
    setPermanentDeleteInlineBlockers([]);
  };

  const handlePermanentDelete = async () => {
    if (permanentDeleteTarget == null || permanentDeleteSubmitting) {
      return;
    }

    setPermanentDeleteSubmitting(true);
    setPermanentDeleteInlineError(null);
    setPermanentDeleteInlineBlockers([]);
    try {
      await deleteStockPermanent(permanentDeleteTarget.id);
      message.success('Акция удалена полностью');
      if (editingStock?.id === permanentDeleteTarget.id) {
        setModalOpen(false);
        setEditingStock(null);
        setEditInlineError(null);
        setEditInlineBlockers([]);
      }
      closePermanentDeleteDialog(true);
      fetchData();
    } catch (err: unknown) {
      const blocked = getStockMutationBlockedResponse(err);
      if (blocked) {
        setPermanentDeleteInlineError(blocked.message);
        setPermanentDeleteInlineBlockers(blocked.diagnostics.blockers);
        return;
      }
      setPermanentDeleteInlineError(getStockPermanentDeleteErrorMessage(err));
    } finally {
      setPermanentDeleteSubmitting(false);
    }
  };

  const handleSetTracking = async (stock: Stock, tracked: boolean) => {
    setTrackingLoadingByStock((prev) => ({ ...prev, [stock.id]: true }));
    try {
      if (tracked) {
        await trackStock(stock.id);
        message.success('Акция добавлена в отслеживаемые');
      } else {
        await untrackStock(stock.id);
        message.success('Акция удалена из отслеживаемых');
      }
      fetchData();
    } catch {
      message.error(tracked ? 'Ошибка добавления в отслеживаемые' : 'Ошибка удаления из отслеживаемых');
    } finally {
      setTrackingLoadingByStock((prev) => ({ ...prev, [stock.id]: false }));
    }
  };

  const handleFetchLivePrice = async (stock: Stock) => {
    if (!stock.ticker?.trim()) return;
    setLivePrices((prev) => ({ ...prev, [stock.id]: preserveEntry(prev[stock.id], true) }));
    try {
      const priceRes = await getStockPrice(stock.ticker, stock.exchange, stock.finanzenNetSlug);
      const quote = priceRes.data;

      setLivePrices((prev) => ({
        ...prev,
        [stock.id]: { quote, loading: false },
      }));

      const persisted = await persistConvertedPrice(stock, quote);

      if (persisted != null) {
        const applyPatch = (candidate: Stock) =>
          candidate.id === stock.id
            ? applyPersistedQuoteSnapshot(candidate, persisted)
            : candidate;
        setStocks((prev) => prev.map(applyPatch));
        stocksRef.current = stocksRef.current.map(applyPatch);
      }

      if (isQuoteDelayed(quote)) {
        const tsDisplay = quote.priceTimestampUtc
          ? dayjs.utc(quote.priceTimestampUtc).local().format(PRICE_TIME_FORMAT)
          : '—';
        message.warning(`Задержанная котировка для ${stock.ticker}: ${tsDisplay}`);
        return;
      }
    } catch {
      setLivePrices((prev) => ({ ...prev, [stock.id]: preserveEntry(prev[stock.id], false) }));
      message.error(`Ошибка получения цены для ${stock.ticker}`);
    }
  };

  const TOTAL_COLS = isCatalogMode
    ? (isCatalogPeriodSortMode(selectedSortMode) ? CATALOG_WITH_PERF_TOTAL_COLS : CATALOG_TOTAL_COLS)
    : (isCatalogPeriodSortMode(selectedSortMode) ? STOCKS_TABLE_TOTAL_COLS + 1 : STOCKS_TABLE_TOTAL_COLS);

  const formatEur = (v: number | null | undefined) => fmtCur(v, '€');
  const formatPct = (v: number | null | undefined) => formatPercent(v);
  const columns = [
    {
      title: 'Тикер',
      dataIndex: 'ticker',
      key: 'ticker',
      width: TICKER_COL_WIDTH,
      render: (_ticker: string, record: TableRow) => {
        if (isChartRow(record)) {
          const stock = stocks.find((s) => s.id === record._stockId);
          const live = livePrices[record._stockId];
          return {
            children: (
              <StockPriceChart
                panelId={`chart-panel-${record._stockId}`}
                stockId={record._stockId}
                ticker={stock?.ticker ?? ''}
                name={stock?.name ?? ''}
                exchange={stock?.exchange ?? null}
                providerSymbol={stock?.providerSymbol ?? null}
                wkn={stock?.wkn ?? null}
                isin={stock?.isin ?? null}
                finanzenNetSlug={stock?.finanzenNetSlug ?? null}
                liveQuote={live?.quote ?? null}
                storedPriceEur={stock?.currentPrice ?? null}
                storedPriceChangeEur={stock?.currentPriceChange ?? null}
                storedPriceTimestampUtc={stock?.currentPriceAt ?? null}
              />
            ),
            props: { colSpan: TOTAL_COLS },
          };
        }
        const stock = record as Stock;
        const isExpanded = expandedStockId === stock.id;
        return (
          <div style={CELL_NOWRAP_STYLE}>
            <Tooltip title={stock.ticker}>
              <button
                type="button"
                onClick={() => handleTickerClick(stock.id)}
                aria-expanded={isExpanded}
                aria-controls={`chart-panel-${stock.id}`}
                aria-label={isExpanded ? `Закрыть график цены: ${stock.ticker}` : `Открыть график цены: ${stock.ticker}`}
                style={{
                  padding: 0,
                  background: 'none',
                  border: 'none',
                  cursor: 'pointer',
                  fontWeight: 600,
                  color: isExpanded ? '#1677ff' : 'inherit',
                  display: 'inline-flex',
                  alignItems: 'center',
                  gap: 4,
                  minWidth: 0,
                  maxWidth: TICKER_TEXT_MAX_WIDTH,
                }}
              >
                <CaretRightFilled
                  style={{
                    fontSize: 10,
                    transition: 'transform 0.2s',
                    transform: isExpanded ? 'rotate(90deg)' : 'rotate(0deg)',
                    color: '#1677ff',
                    flex: '0 0 auto',
                  }}
                />
                <span style={ELLIPSIS_STYLE}>
                  {stock.ticker}
                </span>
              </button>
            </Tooltip>
            <StockExchangeTag exchange={stock.exchange} />
          </div>
        );
      },
    },
    {
      title: 'Название',
      dataIndex: 'name',
      key: 'name',
      width: NAME_COL_WIDTH,
      render: (name: string, record: TableRow) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const stock = record as Stock;
        return (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <div style={{ ...CELL_BASE_STYLE, gap: 8 }}>
              <Text style={{ ...FLEX_MIN_WIDTH_STYLE, fontSize: 16 }} ellipsis={{ tooltip: name }}>{name}</Text>
              <StockClassificationBadges
                sector={stock.industry?.sector?.name ?? stock.sector?.name ?? null}
                industry={stock.industry?.name ?? null}
              />
            </div>
            {stock.commonName && stock.commonName !== name && (
              <Text type="secondary" style={{ fontSize: 16 }} ellipsis={{ tooltip: stock.commonName }}>
                {stock.commonName}
              </Text>
            )}
          </div>
        );
      },
    },
    ...(isCatalogMode ? [
      {
        title: 'Индексы',
        key: 'indices',
        width: INDEX_MEMBERSHIP_COL_WIDTH,
        render: (_: unknown, record: TableRow) => {
          if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
          const stock = record as Stock;
          const ids = stock.marketIndexIds ?? [];
          if (ids.length === 0) {
            return <Text type="secondary">—</Text>;
          }
          return (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {ids.map((indexId) => (
                <Tag key={indexId}>{marketIndexNameById.get(indexId) ?? `#${indexId}`}</Tag>
              ))}
            </div>
          );
        },
      },
    ] : []),
    ...(isCatalogPeriodSortMode(selectedSortMode) ? [
      {
        title: 'Рост за период',
        key: 'performance',
        align: 'right' as const,
        width: PERFORMANCE_COL_WIDTH,
        render: (_: unknown, record: TableRow) => {
          if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
          const stock = record as Stock;
          if (performanceLoading) {
            return <span style={{ color: '#8c8c8c' }}>…</span>;
          }
          const perf = formatPerformance(performanceMap.get(stock.id));
          if (perf.kind === 'unavailable') {
            return (
              <Tooltip title="Недостаточно исторических данных">
                <span style={{ color: '#8c8c8c' }}>—</span>
              </Tooltip>
            );
          }
          return <span style={{ color: perf.color, whiteSpace: 'nowrap' }}>{perf.formatted}</span>;
        },
      },
    ] : []),
    {
      title: 'Текущая цена',
      key: 'savedPrice',
      align: 'right' as const,
      width: SAVED_PRICE_COL_WIDTH,
      render: (_: unknown, record: TableRow) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const stock = record as Stock;
        const selectedSnapshot = getSelectedSnapshot(stock);
        return (
          <span style={{ whiteSpace: 'nowrap', fontWeight: 500 }}>
            {formatEur(selectedSnapshot.currentPrice)}
          </span>
        );
      },
    },
    {
      title: 'Изменение (€)',
      key: 'changeEur',
      align: 'right' as const,
      width: CHANGE_EUR_COL_WIDTH,
      className: STOCKS_CHANGE_COMPACT_CLASS,
      render: (_: unknown, record: TableRow) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const stock = record as Stock;
        const selectedSnapshot = getSelectedSnapshot(stock);
        const change = selectedSnapshot.currentPriceChange ?? null;
        const color =
          change == null ? '#8c8c8c' : change > 0 ? COLOR_POSITIVE : change < 0 ? COLOR_NEGATIVE : '#8c8c8c';
        return (
          <span style={{ color, whiteSpace: 'nowrap' }}>
            {fmtCur(change, '€', { signed: true })}
          </span>
        );
      },
    },
    {
      title: '(%)',
      key: 'changePct',
      width: CHANGE_PCT_COL_WIDTH,
      className: STOCKS_CHANGE_COMPACT_CLASS,
      render: (_: unknown, record: TableRow) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const stock = record as Stock;
        const selectedSnapshot = getSelectedSnapshot(stock);
        const pct = selectedSnapshot.currentPriceChangePercent ?? null;
        const color =
          pct == null ? '#8c8c8c' : pct > 0 ? COLOR_POSITIVE : pct < 0 ? COLOR_NEGATIVE : '#8c8c8c';
        return (
          <span style={{ color, whiteSpace: 'nowrap' }}>
            {formatPct(pct)}
          </span>
        );
      },
    },
    {
      title: 'Цена API',
      key: 'apiPrice',
      align: 'right' as const,
      width: API_PRICE_COL_WIDTH,
      className: STOCKS_API_AREA_COMPACT_CLASS,
      render: (_: unknown, record: TableRow) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const stock = record as Stock;
        const live = livePrices[stock.id];
        const quote = live?.quote ?? null;
        const apiPriceText = getApiPriceText(live);
        const normalizedTooltip = getApiPriceTooltip(quote);
        const marketStatus = getMarketStatus(live);
        const delayed = isQuoteDelayed(quote);
        const delayTooltip = (delayed && quote?.delayWarning) ? quote.delayWarning : undefined;
        return (
          <span title={normalizedTooltip} style={{ fontSize: 16, color: '#595959', whiteSpace: 'nowrap', display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            {apiPriceText}
            {delayed ? (
              <Tooltip title={delayTooltip}>
                <Tag
                  color="orange"
                  style={{ fontSize: 16, lineHeight: '22px', padding: '0 3px', marginInlineEnd: 0, cursor: delayTooltip ? 'help' : undefined }}
                  aria-label={delayTooltip ?? STALE_DELAY_LABEL}
                >
                  {STALE_DELAY_LABEL}
                </Tag>
              </Tooltip>
            ) : (
              <>
                {marketStatus === 'open' && (
                  <Tag color="green" style={{ fontSize: 16, lineHeight: '22px', padding: '0 3px', marginInlineEnd: 0 }}>Open</Tag>
                )}
                {marketStatus === 'closed' && (
                  <Tag style={{ fontSize: 16, lineHeight: '22px', padding: '0 3px', marginInlineEnd: 0 }}>Closed</Tag>
                )}
              </>
            )}
          </span>
        );
      },
    },
    {
      title: 'Время',
      key: 'priceTime',
      width: PRICE_TIME_COL_WIDTH,
      className: STOCKS_API_AREA_COMPACT_CLASS,
      render: (_: unknown, record: TableRow) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const stock = record as Stock;
        const selectedSnapshot = getSelectedSnapshot(stock);
        const ts = selectedSnapshot.currentPriceAt;
        if (!ts) return <span style={{ whiteSpace: 'nowrap' }}>—</span>;
        return <span style={{ whiteSpace: 'nowrap' }}>{dayjs.utc(ts).local().format(PRICE_TIME_FORMAT)}</span>;
      },
    },
    {
      title: 'Действия',
      key: 'actions',
      width: ACTIONS_COL_WIDTH,
      className: STOCKS_API_AREA_COMPACT_CLASS,
      render: (_: unknown, record: TableRow) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const stock = record as Stock;
        const live = livePrices[stock.id];
        const isProtectedStock = portfolioStockIds.has(stock.id);
        const isTracked = stock.trackingStatus !== TRACKING_STATUS_CATALOG_ONLY;
        const trackingLoading = trackingLoadingByStock[stock.id] === true;
        return renderStockRowActions({
          stock,
          live,
          isProtectedStock,
          onRefresh: handleFetchLivePrice,
          onOpenFundamentals: (selectedStock) => setFundamentalsStock(selectedStock),
          onOpenEdit: openEditModal,
          onDelete: handleDelete,
          trackingAction: isCatalogMode ? (
            <Space size={6}>
              <Tooltip title={isTracked ? 'Акция уже отслеживается' : 'Добавить в отслеживаемые'}>
                <span>
                  <Button
                    icon={<StarOutlined />}
                    size="small"
                    aria-label={isTracked ? 'Акция уже отслеживается' : 'Добавить в отслеживаемые'}
                    disabled={isTracked || trackingLoading}
                    loading={trackingLoading}
                    onClick={!isTracked && !trackingLoading ? () => handleSetTracking(stock, true) : undefined}
                  />
                </span>
              </Tooltip>
            </Space>
          ) : undefined,
        });
      },
    },
  ];

  const makeGroupRows = useCallback((group: Stock[]): TableRow[] => {
    const rows: TableRow[] = [];
    for (const stock of group) {
      rows.push(stock);
      if (expandedStockId === stock.id) {
        rows.push({ _isChartRow: true, _stockId: stock.id });
      }
    }
    return rows;
  }, [expandedStockId]);

  const portfolioRows = useMemo(() => makeGroupRows(portfolioGroup), [makeGroupRows, portfolioGroup]);
  const fraRows = useMemo(() => makeGroupRows(fraGroup), [makeGroupRows, fraGroup]);
  const nyseRows = useMemo(() => makeGroupRows(nyseGroup), [makeGroupRows, nyseGroup]);

  const handleTickerClick = (stockId: number) => {
    setExpandedStockId((prev) => (prev === stockId ? null : stockId));
  };

  const getTableRowKey = useCallback(
    (record: TableRow) => isChartRow(record) ? `chart-${record._stockId}` : String((record as Stock).id),
    [],
  );

  const renderExpandedChart = useCallback((stock: Stock) => {
    const live = livePrices[stock.id];
    return (
      <StockPriceChart
        panelId={`chart-panel-${stock.id}`}
        stockId={stock.id}
        ticker={stock.ticker}
        name={stock.name}
        exchange={stock.exchange}
        providerSymbol={stock.providerSymbol ?? null}
        wkn={stock.wkn ?? null}
        isin={stock.isin ?? null}
        finanzenNetSlug={stock.finanzenNetSlug ?? null}
        liveQuote={live?.quote ?? null}
        storedPriceEur={stock.currentPrice ?? null}
        storedPriceChangeEur={stock.currentPriceChange ?? null}
        storedPriceTimestampUtc={stock.currentPriceAt ?? null}
      />
    );
  }, [livePrices]);

  const renderGroup = (groupTitle: string, groupStocks: Stock[], rows: TableRow[]) => {
    if (groupStocks.length === 0) return null;
    return (
      <div key={groupTitle} style={{ marginBottom: 24, border: '1px solid #d9d9d9', borderRadius: 8, overflow: 'hidden' }}>
        <div style={{ padding: '8px 16px', borderBottom: '1px solid #A9C3D6', background: '#A9C3D6', display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          <InfoCircleOutlined aria-hidden="true" style={{ color: '#ffffff', flexShrink: 0 }} />
          <Title level={5} style={{ margin: 0, color: '#ffffff', fontWeight: 600 }}>{groupTitle}</Title>
          <Tag>{groupStocks.length}</Tag>
        </div>
        <Table
          className="stocks-table"
          dataSource={rows}
          columns={columns}
          rowKey={getTableRowKey}
          tableLayout="fixed"
          scroll={{ x: STOCKS_TABLE_SCROLL_X + (isCatalogPeriodSortMode(selectedSortMode) ? PERFORMANCE_COL_WIDTH : 0) }}
          pagination={false}
          rowClassName={(record: TableRow) => {
            if (isChartRow(record)) return 'chart-panel-row';
            return portfolioStockIds.has((record as Stock).id) ? PORTFOLIO_ROW_CLASS : '';
          }}
        />
      </div>
    );
  };

  return (
    <>
      <AuthenticatedShell
        portfolios={portfolios}
        selectedKeys={[isCatalogMode ? 'stocks-catalog' : 'stocks-list']}
        marketIndices={marketIndices}
        userName={user?.username}
        onLogout={logout}
        headerLeft={(
          <Title level={4} style={{ margin: 0 }}>
            {isCatalogMode ? 'Список акций' : 'Отслеживаемые акции'}
          </Title>
        )}
        headerRight={(
          <div data-testid="stocks-page-header-actions" style={{ display: 'flex', alignItems: 'center', gap: 12, minWidth: 0 }}>
            <Button
              data-testid="stocks-page-add-stock-button"
              type="primary"
              icon={<PlusOutlined />}
              onClick={openCreateModal}
            >
              Добавить акцию
            </Button>
          </div>
        )}
      >
        <div
          data-testid="stocks-page-toolbar-row"
          style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap', marginBottom: 16 }}
        >
          <Input
            placeholder="Поиск: тикер, название, биржа, индекс"
            value={selectedQuery}
            onChange={(event) => (isCatalogMode
              ? handleCatalogQueryChange(event.target.value)
              : handleTrackedQueryChange(event.target.value))}
            allowClear
            size="small"
            style={{ width: 320, maxWidth: '100%' }}
          />
          <AdvancedStockFilterToolbarControls
            compact
            activeGroupCount={activeAdvancedFilterGroups}
            onOpen={isCatalogMode ? openCatalogAdvancedFilters : openTrackedAdvancedFilters}
            onReset={isCatalogMode ? resetCatalogAdvancedFilters : resetTrackedAdvancedFilters}
            resetDisabled={activeAdvancedFilterGroups === 0}
          />
          <Space size={4} align="center" wrap>
            <span style={{ fontSize: 16, color: '#595959', whiteSpace: 'nowrap' }}>Сортировка:</span>
            <Select<CatalogSortMode>
              size="small"
              value={selectedSortMode}
              onChange={isCatalogMode ? handleCatalogSortModeChange : handleTrackedSortModeChange}
              options={CATALOG_SORT_MODE_OPTIONS}
              style={{ width: 130 }}
              aria-label="Сортировка"
            />
            {isCatalogPeriodSortMode(selectedSortMode) && (
              <Tooltip title={selectedSortDirection === 'desc' ? 'Убыванию' : 'Возрастанию'}>
                <Button
                  size="small"
                  icon={selectedSortDirection === 'desc' ? <SortDescendingOutlined /> : <SortAscendingOutlined />}
                  onClick={isCatalogMode ? handleCatalogSortDirectionToggle : handleTrackedSortDirectionToggle}
                  aria-label={selectedSortDirection === 'desc' ? 'Сортировать по возрастанию' : 'Сортировать по убыванию'}
                />
              </Tooltip>
            )}
          </Space>
          {!isCatalogMode && (
            <>
              <Text type="secondary" style={{ fontSize: 16 }}>
                Авто-обновление через {formatCountdown(countdown)}
              </Text>
              <Button
                icon={<ReloadOutlined />}
                loading={refreshing}
                onClick={() => { handleRefreshPrices(false); setCountdown(AUTO_REFRESH_INTERVAL); }}
              >
                Обновить цены
              </Button>
            </>
          )}
        </div>
        {loading ? (
          <div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}>
            <Spin size="large" />
          </div>
        ) : isCatalogMode ? (
          filteredStocks.length === 0 ? (
            <Text type="secondary">Нет акций по выбранным фильтрам</Text>
          ) : (
            <>
              {performanceError && isCatalogPeriodSortMode(selectedSortMode) && (
                <div style={{ marginBottom: 8, color: '#cf1322' }}>{performanceError}</div>
              )}
              <Table
                className="stocks-table"
                dataSource={filteredStocks}
                columns={columns}
                rowKey={getTableRowKey}
                tableLayout="fixed"
                scroll={{ x: STOCKS_TABLE_SCROLL_X + INDEX_MEMBERSHIP_COL_WIDTH + (isCatalogPeriodSortMode(selectedSortMode) ? PERFORMANCE_COL_WIDTH : 0) }}
                expandable={{
                  expandedRowKeys: expandedStockId != null ? [String(expandedStockId)] : [],
                  expandedRowRender: (stock) => renderExpandedChart(stock as Stock),
                  expandIcon: () => null,
                }}
                pagination={{
                  current: catalogPage,
                  pageSize: CATALOG_PAGE_SIZE,
                  showSizeChanger: false,
                  showTotal: (total) => `Всего: ${total}`,
                  onChange: handleCatalogPageChange,
                }}
                rowClassName={(record: TableRow) => {
                  if (isChartRow(record)) return 'chart-panel-row';
                  return '';
                }}
              />
            </>
          )
        ) : (
          <>
            {performanceError && isCatalogPeriodSortMode(selectedSortMode) && (
              <div style={{ marginBottom: 8, color: '#cf1322' }}>{performanceError}</div>
            )}
            {portfolioGroup.length === 0 && fraGroup.length === 0 && nyseGroup.length === 0 ? (
              <Text type="secondary">Нет акций по выбранным фильтрам</Text>
            ) : (
              <>
                {renderGroup('Портфель', portfolioGroup, portfolioRows)}
                {renderGroup('Цены на франкфуртской бирже', fraGroup, fraRows)}
                {renderGroup('Цены на нью-йоркской бирже', nyseGroup, nyseRows)}
              </>
            )}
          </>
        )}
      </AuthenticatedShell>
      <StockFundamentalsDrawer
        stock={fundamentalsStock}
        open={fundamentalsStock !== null}
        onClose={() => setFundamentalsStock(null)}
      />
      <AdvancedStockFiltersDrawer
        open={isCatalogMode ? catalogFiltersOpen : trackedFiltersOpen}
        draftFilters={isCatalogMode ? catalogAdvancedDraft : trackedAdvancedDraft}
        sectorOptions={sectorFilterOptions}
        industryOptions={industryFilterOptions}
        onClose={isCatalogMode ? closeCatalogAdvancedFilters : closeTrackedAdvancedFilters}
        onDraftChange={isCatalogMode ? handleCatalogAdvancedDraftChange : handleTrackedAdvancedDraftChange}
        onClearDraft={isCatalogMode ? clearCatalogAdvancedDraft : clearTrackedAdvancedDraft}
        onApply={isCatalogMode ? applyCatalogAdvancedFilters : applyTrackedAdvancedFilters}
      />
      <StockEditModal
        open={modalOpen}
        mode={editingStock ? 'edit' : 'create'}
        stock={editingStock}
        sectors={sectors}
        marketIndices={marketIndices}
        submitting={submitting}
        inlineError={editInlineError}
        inlineBlockers={editInlineBlockers}
        onCancel={() => {
          setModalOpen(false);
          setEditingStock(null);
          setEditInlineError(null);
          setEditInlineBlockers([]);
        }}
        onSubmit={handleSubmit}
        onPermanentDelete={editingStock ? () => showPermanentDeleteDialog(editingStock) : undefined}
      />
      <Modal
        open={permanentDeleteTarget != null}
        title="Удалить акцию полностью?"
        okText="Удалить"
        cancelText="Отмена"
        okButtonProps={{ danger: true, loading: permanentDeleteSubmitting, disabled: permanentDeleteSubmitting }}
        cancelButtonProps={{ disabled: permanentDeleteSubmitting }}
        onOk={handlePermanentDelete}
        onCancel={() => closePermanentDeleteDialog()}
        destroyOnHidden
      >
        <div style={{ display: 'grid', gap: 10 }}>
          <Typography.Text>
            История котировок, фундаментальные и технические данные будут удалены.
          </Typography.Text>
          {(permanentDeleteInlineError || permanentDeleteInlineBlockers.length > 0) && (
            <Alert
              type="error"
              showIcon
              message={permanentDeleteInlineError ?? 'Удаление заблокировано.'}
              description={permanentDeleteInlineBlockers.length > 0 ? renderBlockersList(permanentDeleteInlineBlockers) : undefined}
            />
          )}
        </div>
      </Modal>
    </>
  );
};

export default StocksPage;
