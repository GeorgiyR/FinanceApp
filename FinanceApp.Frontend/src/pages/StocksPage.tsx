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
  Select,
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
  updateStockMetadata,
  updateStockQuote,
  getTrackedStocks,
  getPortfolios,
  getStockPrice,
  trackStock,
  untrackStock,
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
  UpdateStockQuoteRequest,
} from '../types';
import { groupStocks } from '../utils/stockGrouping';
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

export const getStockDeleteErrorMessage = (err: unknown): string => {
  if (axios.isAxiosError(err) && typeof err.response?.data === 'string' && err.response.data.trim().length > 0) {
    return err.response.data;
  }

  return STOCK_DELETE_GENERIC_ERROR;
};

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
  const [performanceMap, setPerformanceMap] = useState<PerformanceMap>(new Map());
  const [performanceLoading, setPerformanceLoading] = useState(false);
  const [performanceError, setPerformanceError] = useState<string | null>(null);
  const { user, logout } = useAuth();
  const stocksRef = useRef<Stock[]>([]);
  const performanceAbortRef = useRef<AbortController | null>(null);
  const performanceRequestIdRef = useRef(0);
  const catalogSortModeRef = useRef<CatalogSortMode>(CATALOG_SORT_NAME_MODE);
  catalogSortModeRef.current = catalogSortMode;
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
  const filteredStocks = useMemo(() => {
    if (!isCatalogMode) {
      return stocks;
    }

    const query = catalogQuery.trim().toLowerCase();
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

    if (isCatalogPeriodSortMode(catalogSortMode)) {
      const dir = catalogSortDirection === 'asc' ? 1 : -1;
      return [...base].sort((a, b) => {
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

    return [...base].sort((a, b) => {
      const nameA = (a.commonName || a.name || '').trim();
      const nameB = (b.commonName || b.name || '').trim();
      const cmp = nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
      return cmp !== 0 ? cmp : a.ticker.localeCompare(b.ticker, undefined, { sensitivity: 'base' });
    });
  }, [catalogQuery, catalogSortMode, catalogSortDirection, performanceMap, isCatalogMode, marketIndexNameById, stocks]);
  const { portfolioGroup, fraGroup, nyseGroup } = useMemo(
    () => groupStocks(filteredStocks, portfolioStockIds),
    [filteredStocks, portfolioStockIds],
  );
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

  const handleCatalogSortModeChange = useCallback((mode: CatalogSortMode) => {
    setCatalogSortMode(mode);
    setCatalogPage(1);
    if (!isCatalogPeriodSortMode(mode)) {
      clearPerformanceStateAndAbort();
    } else {
      void loadPerformance(mode);
    }
  }, [clearPerformanceStateAndAbort, loadPerformance]);

  const handleCatalogSortDirectionToggle = useCallback(() => {
    setCatalogSortDirection((prev) => (prev === 'desc' ? 'asc' : 'desc'));
  }, []);

  useEffect(() => {
    if (!isCatalogMode) return;
    return () => { performanceAbortRef.current?.abort(); };
  }, [isCatalogMode]);

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
    setModalOpen(true);
  };

  const openEditModal = (stock: Stock) => {
    setEditingStock(stock);
    setModalOpen(true);
  };

  const handleSubmit = async (values: Parameters<typeof buildUpdateStockMetadataPayload>[0]) => {
    setSubmitting(true);
    try {
      if (editingStock) {
        await updateStockMetadata(editingStock.id, buildUpdateStockMetadataPayload(values));
        message.success('Акция обновлена');
      } else {
        await createStock(buildCreateStockPayload(values));
        message.success('Акция добавлена');
      }
      setModalOpen(false);
      setEditingStock(null);
      fetchData();
    } catch (err: unknown) {
      const errorMsg =
        err != null &&
        typeof err === 'object' &&
        'response' in err &&
        err.response != null &&
        typeof err.response === 'object' &&
        'data' in err.response &&
        typeof err.response.data === 'string'
          ? err.response.data
          : 'Ошибка сохранения акции';
      message.error(errorMsg);
    } finally {
      setSubmitting(false);
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
    ? (isCatalogPeriodSortMode(catalogSortMode) ? CATALOG_WITH_PERF_TOTAL_COLS : CATALOG_TOTAL_COLS)
    : STOCKS_TABLE_TOTAL_COLS;

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
      ...(isCatalogPeriodSortMode(catalogSortMode) ? [
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
          scroll={{ x: STOCKS_TABLE_SCROLL_X }}
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
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
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
            {isCatalogMode && (
              <>
                <Select<CatalogSortMode>
                  value={catalogSortMode}
                  onChange={handleCatalogSortModeChange}
                  options={CATALOG_SORT_MODE_OPTIONS}
                  style={{ width: 130 }}
                  aria-label="Сортировка"
                />
                {isCatalogPeriodSortMode(catalogSortMode) && (
                  <Tooltip title={catalogSortDirection === 'desc' ? 'Убыванию' : 'Возрастанию'}>
                    <Button
                      icon={catalogSortDirection === 'desc' ? <SortDescendingOutlined /> : <SortAscendingOutlined />}
                      onClick={handleCatalogSortDirectionToggle}
                      aria-label={catalogSortDirection === 'desc' ? 'Сортировать по возрастанию' : 'Сортировать по убыванию'}
                    />
                  </Tooltip>
                )}
                <Input
                  placeholder="Поиск: тикер, название, биржа, индекс"
                  value={catalogQuery}
                  onChange={(event) => setCatalogQuery(event.target.value)}
                  allowClear
                  style={{ width: 320 }}
                />
              </>
            )}
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={openCreateModal}
            >
              Добавить акцию
            </Button>
          </div>
        )}
      >
        {loading ? (
          <div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}>
            <Spin size="large" />
          </div>
        ) : isCatalogMode ? (
          filteredStocks.length === 0 ? (
            <Text type="secondary">Нет акций по выбранным фильтрам</Text>
          ) : (
            <>
              {performanceError && (
                <div style={{ marginBottom: 8, color: '#cf1322' }}>{performanceError}</div>
              )}
              <Table
                className="stocks-table"
                dataSource={filteredStocks}
                columns={columns}
                rowKey={getTableRowKey}
                tableLayout="fixed"
                scroll={{ x: STOCKS_TABLE_SCROLL_X + INDEX_MEMBERSHIP_COL_WIDTH + (isCatalogPeriodSortMode(catalogSortMode) ? PERFORMANCE_COL_WIDTH : 0) }}
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
                  onChange: (page) => setCatalogPage(page),
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
      <StockEditModal
        open={modalOpen}
        mode={editingStock ? 'edit' : 'create'}
        stock={editingStock}
        sectors={sectors}
        marketIndices={marketIndices}
        submitting={submitting}
        onCancel={() => { setModalOpen(false); setEditingStock(null); }}
        onSubmit={handleSubmit}
      />
    </>
  );
};

export default StocksPage;
