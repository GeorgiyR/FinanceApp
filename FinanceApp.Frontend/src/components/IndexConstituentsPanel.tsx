import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Empty,
  Input,
  Select,
  Space,
  Spin,
  Table,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd';
import {
  SearchOutlined,
  PlusOutlined,
  ReloadOutlined,
  CaretRightFilled,
  FundOutlined,
  EditOutlined,
  BarChartOutlined,
  SyncOutlined,
} from '@ant-design/icons';
import type { ColumnsType } from 'antd/es/table';
import axios from 'axios';
import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
import {
  getIndexConstituentHistory,
  getIndexConstituents,
  getIndexConstituentHistoryRefreshJob,
  getIndexConstituentPerformance,
  getStock,
  getStockPrice,
  refreshIndexConstituentHistory,
  refreshIndexConstituents,
  refreshIndexConstituentsHistory,
  startIndexConstituentsBatchQuoteRefresh,
  getIndexConstituentsBatchQuoteRefreshJob,
  trackStock,
  updateStockMetadata,
  updateStockQuote,
} from '../services/api';
import StockEditModal, {
  buildUpdateStockMetadataPayload,
  loadStockMetadataLookups,
} from './StockEditModal';
import StockClassificationBadges from './StockClassificationBadges';
import StockExchangeTag from './StockExchangeTag';
import StockFundamentalsDrawer from './StockFundamentalsDrawer';
import StockPriceChart from './StockPriceChart';
import { formatCurrency as fmtCur, formatPercent } from '../utils/currency';
import { isQuoteDelayed } from '../utils/quote';
import { applyPersistedQuoteSnapshot, buildQuotePatch } from '../utils/quotePersistence';
import {
  resolveNewestCurrentPriceSnapshot,
  shouldKeepLiveOverlayAfterPersistedRefresh,
} from '../utils/currentPriceSnapshot';
import type {
  IndexConstituentDto,
  IndexConstituentHistoryRefreshBatchResponse,
  IndexConstituentHistoryRefreshJobState,
  IndexConstituentsRefreshResponse,
  MarketIndex,
  SectorDto,
  Stock,
  StockHistoryRange,
  StockQuoteResponse,
  UpdateStockQuoteRequest,
  UpdateStockQuoteResponse,
} from '../types';
import { StockTrackingStatus } from '../types';
import { INDEX_HISTORY_JOB_POLL_INTERVAL_MS, INDEX_HISTORY_JOB_POLL_TIMEOUT_MS } from './indexConstituentHistoryRefresh';
import { STOCK_HISTORY_RANGE_OPTIONS } from './historyRangeOptions';
import {
  formatPerformance,
  sortConstituentsByName,
  sortConstituentsByPerformance,
} from './performanceHelpers';
import type { PerformanceMap } from './performanceHelpers';
import {
  INDEX_BATCH_QUOTE_JOB_POLL_INTERVAL_MS,
  INDEX_BATCH_QUOTE_JOB_POLL_TIMEOUT_MS,
  runIndexConstituentsBatchQuoteRefreshJob,
} from './indexConstituentsBatchQuoteRefresh';

const { Text } = Typography;
dayjs.extend(utc);

export const UNSUPPORTED_REFRESH_MESSAGE_FALLBACK =
  'Автоматическая загрузка состава для этого индекса не поддерживается';

export interface IndexConstituentsPanelProps {
  indexId: number;
  isArchived: boolean;
}

interface SourceMeta {
  source: string | null;
  asOfDate: string | null;
  isCuratedSnapshot: boolean;
  isStale: boolean;
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function getNonEmptyString(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

export function isIndexConstituentsRefreshResponse(
  value: unknown,
): value is IndexConstituentsRefreshResponse {
  if (!isObjectRecord(value)) return false;
  if (typeof value.marketIndexId !== 'number') return false;
  if (typeof value.providerStatus !== 'string') return false;
  if (typeof value.added !== 'number') return false;
  if (typeof value.updated !== 'number') return false;
  if (typeof value.unchanged !== 'number') return false;
  if (typeof value.closed !== 'number') return false;
  if ('providerMessage' in value) {
    const providerMessage = value.providerMessage;
    if (providerMessage != null && typeof providerMessage !== 'string') return false;
  }
  return true;
}

function getProviderMessageFromBody(data: unknown): string | null {
  if (!isObjectRecord(data)) return null;
  return getNonEmptyString(data.providerMessage);
}

export function getErrMsg(err: unknown, fallback: string): string {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data;
    const providerMessage = getProviderMessageFromBody(data);
    if (providerMessage) return providerMessage;
    const rawMessage = getNonEmptyString(data);
    if (rawMessage) return rawMessage;
    if (isObjectRecord(data)) {
      const message = getNonEmptyString(data.message);
      if (message) return message;
    }
  }
  return fallback;
}

export type RefreshResultNotice =
  | { kind: 'warning'; message: string; shouldReload: false }
  | { kind: 'success'; message: string; shouldReload: true }
  | { kind: 'error'; message: string; shouldReload: false };

export function classifyRefreshResult(
  response: IndexConstituentsRefreshResponse,
): RefreshResultNotice {
  if (response.providerStatus === 'Unsupported') {
    return {
      kind: 'warning',
      message: getNonEmptyString(response.providerMessage) ?? UNSUPPORTED_REFRESH_MESSAGE_FALLBACK,
      shouldReload: false,
    };
  }

  if (response.providerStatus === 'Success' || response.providerStatus === 'Partial') {
    const conflicts = response.conflicts ?? 0;
    const conflictsPart = conflicts > 0 ? `, конфликтов: ${conflicts}` : '';
    return {
      kind: 'success',
      message: `Добавлено: ${response.added}, без изменений: ${response.unchanged}, закрыто: ${response.closed}${conflictsPart}`,
      shouldReload: true,
    };
  }

  return {
    kind: 'error',
    message: getNonEmptyString(response.providerMessage) ?? 'Ошибка загрузки от поставщика',
    shouldReload: false,
  };
}

export function classifyRefreshError(err: unknown, fallback: string): RefreshResultNotice {
  if (axios.isAxiosError(err) && err.response?.status === 422) {
    const responseData = err.response.data;
    if (isIndexConstituentsRefreshResponse(responseData)) {
      if (responseData.providerStatus === 'Unsupported') {
        return {
          kind: 'warning',
          message:
            getNonEmptyString(responseData.providerMessage) ?? UNSUPPORTED_REFRESH_MESSAGE_FALLBACK,
          shouldReload: false,
        };
      }
      return {
        kind: 'error',
        message: getNonEmptyString(responseData.providerMessage) ?? fallback,
        shouldReload: false,
      };
    }
    return { kind: 'error', message: fallback, shouldReload: false };
  }

  return { kind: 'error', message: getErrMsg(err, fallback), shouldReload: false };
}

export function formatBatchHistorySummary(response: IndexConstituentHistoryRefreshBatchResponse): string {
  const suffix = response.stoppedDueToRateLimit
    ? ' Обновление остановлено из-за лимита/паузы поставщика.'
    : '';
  return `История акций: успешно ${response.succeeded}, ошибок ${response.failed}, лимит ${response.rateLimited}, пропущено ${response.skippedRateLimited} из ${response.total}.${suffix}`;
}

type LivePriceEntry = {
  quote: StockQuoteResponse | null;
  loading: boolean;
};

type ChartRow = { _isChartRow: true; _stockId: number };
type TableRow = IndexConstituentDto | ChartRow;

const isChartRow = (record: TableRow): record is ChartRow => '_isChartRow' in record;

const preserveEntry = (current: LivePriceEntry | undefined, loading: boolean): LivePriceEntry => ({
  quote: current?.quote ?? null,
  loading,
});

const TICKER_COL_WIDTH = 220;
const NAME_COL_WIDTH = 300;
const SAVED_PRICE_COL_WIDTH = 130;
const CHANGE_EUR_COL_WIDTH = 108;
const CHANGE_PCT_COL_WIDTH = 75;
const PERFORMANCE_COL_WIDTH = 130;
const API_PRICE_COL_WIDTH = 130;
const PRICE_TIME_COL_WIDTH = 135;
const ACTIONS_COL_WIDTH = 180;
const TICKER_META_SPACE_WIDTH = 70;
const TICKER_TEXT_MAX_WIDTH = TICKER_COL_WIDTH - TICKER_META_SPACE_WIDTH;
const TABLE_SCROLL_X =
  TICKER_COL_WIDTH
  + NAME_COL_WIDTH
  + SAVED_PRICE_COL_WIDTH
  + CHANGE_EUR_COL_WIDTH
  + CHANGE_PCT_COL_WIDTH
  + PERFORMANCE_COL_WIDTH
  + API_PRICE_COL_WIDTH
  + PRICE_TIME_COL_WIDTH
  + ACTIONS_COL_WIDTH;
const PRICE_TIME_FORMAT = 'DD.MM.YY HH:mm';
const COLOR_POSITIVE = '#389e0d';
const COLOR_NEGATIVE = '#cf1322';
const STALE_DELAY_LABEL = 'Задержано';
const TRACKED_TOOLTIP = 'Уже добавлена в список акций';
const ADD_TRACKED_ARIA_LABEL = 'Добавлена в список акций';
const ADD_CATALOG_ARIA_LABEL = 'Добавить в список акций';
const FUNDAMENTALS_ARIA_LABEL = 'Фундаментальные данные';

export type ConstituentSortMode = 'name' | StockHistoryRange;
export const CONSTITUENT_NAME_SORT_MODE: ConstituentSortMode = 'name';
const STOCK_HISTORY_RANGE_SET = new Set<StockHistoryRange>(
  STOCK_HISTORY_RANGE_OPTIONS.map((option) => option.value),
);
export const CONSTITUENT_SORT_MODE_OPTIONS: Array<{ label: string; value: ConstituentSortMode }> = [
  { label: 'По названию', value: CONSTITUENT_NAME_SORT_MODE },
  ...STOCK_HISTORY_RANGE_OPTIONS,
];

export function isStockHistoryRangeSortMode(value: string): value is StockHistoryRange {
  return STOCK_HISTORY_RANGE_SET.has(value as StockHistoryRange);
}

export const INDEX_CONSTITUENTS_TOTAL_COLS = 9;
export const QUOTE_PERSIST_FAILURE_MESSAGE = 'Цена получена, но не удалось сохранить её';
export const QUOTE_NO_EUR_MESSAGE = 'Цена получена, но конвертация в EUR недоступна';

export const getConstituentTableRowKey = (record: TableRow): string =>
  isChartRow(record) ? `chart-${record._stockId}` : String(record.stockId);

export const makeConstituentRows = (
  items: IndexConstituentDto[],
  expandedStockId: number | null,
): TableRow[] => {
  const rows: TableRow[] = [];
  for (const item of items) {
    rows.push(item);
    if (expandedStockId === item.stockId) {
      rows.push({ _isChartRow: true, _stockId: item.stockId });
    }
  }
  return rows;
};

export const getTrackButtonState = (trackingStatus: string, trackingLoading: boolean) => {
  const isTracked = trackingStatus === 'Tracked';
  return {
    isTracked,
    disabled: isTracked || trackingLoading,
    loading: trackingLoading,
    ariaLabel: isTracked ? ADD_TRACKED_ARIA_LABEL : ADD_CATALOG_ARIA_LABEL,
    tooltip: isTracked ? TRACKED_TOOLTIP : 'Добавить в список акций',
  };
};

const getTrackingStatusLabel = (trackingStatus?: StockTrackingStatus): string =>
  trackingStatus === StockTrackingStatus.CatalogOnly ? 'CatalogOnly' : 'Tracked';

export const mergeEditedStockIntoConstituents = (
  constituents: IndexConstituentDto[],
  updatedStock: Stock,
  indexId: number,
): IndexConstituentDto[] => {
  if (!(updatedStock.marketIndexIds ?? []).includes(indexId)) {
    return constituents.filter((item) => item.stockId !== updatedStock.id);
  }

  return constituents.map((item) => (
    item.stockId === updatedStock.id
      ? {
        ...item,
        ticker: updatedStock.ticker,
        providerSymbol: updatedStock.providerSymbol ?? null,
        name: updatedStock.name,
        commonName: updatedStock.commonName,
        sector: updatedStock.sector?.name ?? updatedStock.industry?.sector?.name ?? null,
        industry: updatedStock.industry?.name ?? null,
        exchange: updatedStock.exchange,
        isin: updatedStock.isin ?? null,
        wkn: updatedStock.wkn ?? null,
        finanzenNetSlug: updatedStock.finanzenNetSlug ?? null,
        currentPrice: updatedStock.currentPrice,
        currentPriceChange: updatedStock.currentPriceChange ?? null,
        currentPriceChangePercent: updatedStock.currentPriceChangePercent ?? null,
        currentPriceAt: updatedStock.currentPriceAt ?? null,
        trackingStatus: getTrackingStatusLabel(updatedStock.trackingStatus),
      }
      : item
  ));
};

const removeStateEntry = <T,>(entries: Record<number, T>, stockId: number): Record<number, T> => {
  if (!(stockId in entries)) {
    return entries;
  }

  const next = { ...entries };
  delete next[stockId];
  return next;
};

export const beginConstituentQuoteRefresh = (inFlight: Set<number>, stockId: number): boolean => {
  if (inFlight.has(stockId)) {
    return false;
  }

  inFlight.add(stockId);
  return true;
};

export const finishConstituentQuoteRefresh = (inFlight: Set<number>, stockId: number): void => {
  inFlight.delete(stockId);
};

export const getNoEurQuoteMessage = (
  ticker: string,
  quote: StockQuoteResponse,
): string => {
  const warning = quote.conversionWarning?.trim();
  return warning && warning.length > 0
    ? warning
    : `${QUOTE_NO_EUR_MESSAGE} для ${ticker}`;
};

export const persistFreshConstituentQuote = async ({
  constituent,
  quote,
  persistQuote,
}: {
  constituent: Pick<IndexConstituentDto, 'stockId' | 'ticker'>;
  quote: StockQuoteResponse;
  persistQuote: (stockId: number, patch: UpdateStockQuoteRequest) => Promise<UpdateStockQuoteResponse>;
}): Promise<{
  persisted: UpdateStockQuoteResponse | null;
  warningMessage: string | null;
}> => {
  const patch = buildQuotePatch(quote);
  if (patch == null) {
    return {
      persisted: null,
      warningMessage: getNoEurQuoteMessage(constituent.ticker, quote),
    };
  }

  return {
    persisted: await persistQuote(constituent.stockId, patch),
    warningMessage: null,
  };
};

const getApiPriceCurrency = (quote: StockQuoteResponse | null | undefined): string | null =>
  quote?.currency ?? quote?.normalizedQuoteCurrency ?? null;

const getApiPriceText = (live: LivePriceEntry | null | undefined): string => {
  if (live?.loading) return '...';
  const quote = live?.quote;
  const currency = getApiPriceCurrency(quote);
  if (!quote || !currency) return '—';
  return fmtCur(quote.rawCurrentPrice, currency);
};

const getApiPriceTooltip = (quote: StockQuoteResponse | null | undefined): string | undefined =>
  quote && quote.quoteUnitMultiplier !== 1 && quote.normalizedQuoteCurrency
    ? `Нормализовано: ${quote.normalizedCurrentPrice.toFixed(3)} ${quote.normalizedQuoteCurrency}`
    : undefined;

const getMarketStatus = (live: LivePriceEntry | null | undefined): 'open' | 'closed' | null => {
  if (!live || live.loading || !live.quote) return null;
  return live.quote.marketState === 'REGULAR' ? 'open' : 'closed';
};

const IndexConstituentsPanel: React.FC<IndexConstituentsPanelProps> = ({
  indexId,
  isArchived,
}) => {
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [constituents, setConstituents] = useState<IndexConstituentDto[]>([]);
  const [sourceMeta, setSourceMeta] = useState<SourceMeta>({
    source: null,
    asOfDate: null,
    isCuratedSnapshot: false,
    isStale: false,
  });
  const [search, setSearch] = useState('');
  const [trackingId, setTrackingId] = useState<number | null>(null);
  const [historyRefreshStates, setHistoryRefreshStates] = useState<Record<number, IndexConstituentHistoryRefreshJobState>>({});
  const [batchHistoryRefreshing, setBatchHistoryRefreshing] = useState(false);
  const [chartRefreshTokens, setChartRefreshTokens] = useState<Record<number, number>>({});
  const [livePrices, setLivePrices] = useState<Record<number, LivePriceEntry>>({});
  const [expandedStockId, setExpandedStockId] = useState<number | null>(null);
  const [batchHistorySummary, setBatchHistorySummary] = useState<string | null>(null);
  const [batchQuoteRefreshing, setBatchQuoteRefreshing] = useState(false);
  const [batchQuoteProgress, setBatchQuoteProgress] = useState<{ processed: number; total: number } | null>(null);
  const [batchQuoteRetryWaitText, setBatchQuoteRetryWaitText] = useState<string | null>(null);
  const [batchQuoteSummary, setBatchQuoteSummary] = useState<{ text: string; level: 'success' | 'warning' | 'error' | 'info' } | null>(null);
  const batchQuoteAbortRef = useRef<AbortController | null>(null);
  const [editModalOpen, setEditModalOpen] = useState(false);
  const [editModalLoading, setEditModalLoading] = useState(false);
  const [editingStockId, setEditingStockId] = useState<number | null>(null);
  const [editingStock, setEditingStock] = useState<Stock | null>(null);
  const [editSubmitting, setEditSubmitting] = useState(false);
  const [sectors, setSectors] = useState<SectorDto[]>([]);
  const [marketIndices, setMarketIndices] = useState<MarketIndex[]>([]);
  const [fundamentalsStock, setFundamentalsStock] = useState<{
    id: number;
    ticker: string;
    name: string;
  } | null>(null);
  const [messageApi, contextHolder] = message.useMessage();
  const [sortMode, setSortMode] = useState<ConstituentSortMode>(CONSTITUENT_NAME_SORT_MODE);
  const [performanceMap, setPerformanceMap] = useState<PerformanceMap>(new Map());
  const [performanceLoading, setPerformanceLoading] = useState(false);
  const [performanceError, setPerformanceError] = useState<string | null>(null);
  const performanceAbortRef = useRef<AbortController | null>(null);
  const performanceRequestIdRef = useRef(0);
  const sortModeRef = useRef<ConstituentSortMode>(CONSTITUENT_NAME_SORT_MODE);
  sortModeRef.current = sortMode;
  const quoteRefreshInFlightRef = useRef(new Set<number>());
  const persistIndexQuote = useCallback(async (stockId: number, patch: UpdateStockQuoteRequest) => (
    await updateStockQuote(stockId, patch)
  ).data, []);

  const resetPerformanceState = useCallback(() => {
    setPerformanceMap(new Map());
    setPerformanceLoading(false);
    setPerformanceError(null);
  }, []);

  const clearPerformanceStateAndAbort = useCallback(() => {
    performanceRequestIdRef.current += 1;
    performanceAbortRef.current?.abort();
    performanceAbortRef.current = null;
    resetPerformanceState();
  }, [resetPerformanceState]);

  const loadPerformance = useCallback(async (range: StockHistoryRange) => {
    performanceAbortRef.current?.abort();
    const ctrl = new AbortController();
    const requestId = ++performanceRequestIdRef.current;
    performanceAbortRef.current = ctrl;
    setPerformanceLoading(true);
    setPerformanceMap(new Map());
    setPerformanceError(null);
    try {
      const res = await getIndexConstituentPerformance(indexId, range, ctrl.signal);
      if (ctrl.signal.aborted) return;
      if (performanceRequestIdRef.current !== requestId) return;
      if (sortModeRef.current !== range) return;
      const map = new Map<number, number | null>();
      for (const item of res.data.items) {
        map.set(item.stockId, item.changePercent ?? null);
      }
      setPerformanceMap(map);
    } catch (err) {
      if (ctrl.signal.aborted || axios.isCancel(err)) return;
      if (performanceRequestIdRef.current !== requestId) return;
      if (sortModeRef.current !== range) return;
      setPerformanceError(getErrMsg(err, 'Ошибка загрузки данных о росте'));
    } finally {
      if (!ctrl.signal.aborted && performanceRequestIdRef.current === requestId && sortModeRef.current === range) {
        setPerformanceLoading(false);
      }
    }
  }, [indexId]);

  // Reload performance when indexId changes (loadPerformance is recreated) and on initial mount.
  useEffect(() => {
    resetPerformanceState();
    if (isStockHistoryRangeSortMode(sortModeRef.current)) {
      void loadPerformance(sortModeRef.current);
    }
    return () => { performanceAbortRef.current?.abort(); };
  }, [loadPerformance, resetPerformanceState]);

  const handleSortModeChange = useCallback((mode: ConstituentSortMode) => {
    setSortMode(mode);
    sortModeRef.current = mode;
    if (isStockHistoryRangeSortMode(mode)) {
      void loadPerformance(mode);
      return;
    }

    clearPerformanceStateAndAbort();
  }, [clearPerformanceStateAndAbort, loadPerformance]);

  const loadData = useCallback(async (refetchPerformance = false) => {
    setLoading(true);
    setError(null);
    try {
      const res = await getIndexConstituents(indexId);
      setConstituents(res.data.constituents);
      const persistedByStockId = new Map(
        res.data.constituents.map((constituent) => [constituent.stockId, constituent]),
      );
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
      setExpandedStockId((prev) =>
        prev != null && res.data.constituents.some((c) => c.stockId === prev) ? prev : null,
      );
      setSourceMeta({
        source: getNonEmptyString(res.data.source) ?? null,
        asOfDate: getNonEmptyString(res.data.asOfDate) ?? null,
        isCuratedSnapshot: res.data.isCuratedSnapshot === true,
        isStale: res.data.isStale === true,
      });
      // Reload performance only on explicit user-triggered reloads (add/edit/remove/refresh).
      // The initial mount performance load is handled by the dedicated useEffect below.
      if (refetchPerformance && isStockHistoryRangeSortMode(sortModeRef.current)) {
        void loadPerformance(sortModeRef.current);
      }
    } catch (err) {
      setError(getErrMsg(err, 'Ошибка загрузки состава индекса'));
    } finally {
      setLoading(false);
    }
  }, [indexId, loadPerformance]);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  const ensureEditLookupsLoaded = useCallback(async () => {
    if (sectors.length > 0 || marketIndices.length > 0) {
      return;
    }

    const lookupData = await loadStockMetadataLookups();
    setSectors(lookupData.sectors);
    setMarketIndices(lookupData.marketIndices);
    if (lookupData.marketIndicesLoadFailed) {
      void messageApi.warning('Не удалось загрузить мировые индексы');
    }
  }, [marketIndices.length, messageApi, sectors.length]);

  const handleEditCancel = useCallback(() => {
    if (editSubmitting) {
      return;
    }

    setEditModalOpen(false);
    setEditModalLoading(false);
    setEditingStockId(null);
    setEditingStock(null);
  }, [editSubmitting]);

  const handleOpenEdit = useCallback(async (constituent: IndexConstituentDto) => {
    setEditModalOpen(true);
    setEditModalLoading(true);
    setEditingStockId(constituent.stockId);
    setEditingStock(null);

    try {
      const [stockResponse] = await Promise.all([
        getStock(constituent.stockId),
        ensureEditLookupsLoaded(),
      ]);
      setEditingStock(stockResponse.data);
    } catch (err) {
      setEditModalOpen(false);
      void messageApi.error(getErrMsg(err, 'Ошибка загрузки акции'));
    } finally {
      setEditModalLoading(false);
      setEditingStockId(null);
    }
  }, [ensureEditLookupsLoaded, messageApi]);

  const handleEditSubmit = useCallback(async (values: Parameters<typeof buildUpdateStockMetadataPayload>[0]) => {
    if (editingStock == null) {
      return;
    }

    setEditSubmitting(true);
    try {
      await updateStockMetadata(editingStock.id, buildUpdateStockMetadataPayload(values));
      const freshStock = (await getStock(editingStock.id)).data;

      setConstituents((prev) => mergeEditedStockIntoConstituents(prev, freshStock, indexId));
      setLivePrices((prev) => removeStateEntry(prev, editingStock.id));
      setChartRefreshTokens((prev) => ({
        ...removeStateEntry(prev, editingStock.id),
        [editingStock.id]: (prev[editingStock.id] ?? 0) + 1,
      }));
      setExpandedStockId((prev) => (
        (freshStock.marketIndexIds ?? []).includes(indexId) ? prev : (prev === editingStock.id ? null : prev)
      ));
      setFundamentalsStock((prev) => prev?.id === editingStock.id ? null : prev);
      setEditModalOpen(false);
      setEditingStock(null);
      void messageApi.success('Акция обновлена');
      await loadData(true);
    } catch (err) {
      void messageApi.error(getErrMsg(err, 'Ошибка сохранения акции'));
    } finally {
      setEditSubmitting(false);
    }
  }, [editingStock, indexId, loadData, messageApi]);

  const handleRefresh = async () => {
    setRefreshing(true);
    try {
      const res = await refreshIndexConstituents(indexId);
      const result = classifyRefreshResult(res.data);
      if (result.kind === 'warning') {
        void messageApi.warning(result.message);
      } else if (result.kind === 'success') {
        void messageApi.success(result.message);
      } else {
        void messageApi.error(result.message);
      }
      if (result.shouldReload) {
        await loadData(true);
      }
      setSourceMeta((prev) => ({
        source: getNonEmptyString(res.data.providerName) ?? prev.source,
        asOfDate: getNonEmptyString(res.data.asOfDate) ?? prev.asOfDate,
        isCuratedSnapshot: res.data.isCuratedSnapshot === true || prev.isCuratedSnapshot,
        isStale: res.data.isStale === true,
      }));
    } catch (err) {
      const result = classifyRefreshError(err, 'Ошибка обновления состава');
      if (result.kind === 'warning') {
        void messageApi.warning(result.message);
      } else {
        void messageApi.error(result.message);
      }
    } finally {
      setRefreshing(false);
    }
  };

  const handleTrack = async (constituent: IndexConstituentDto) => {
    setTrackingId(constituent.stockId);
    try {
      await trackStock(constituent.stockId);
      void messageApi.success(`«${constituent.name}» добавлена в отслеживаемые акции`);
      setConstituents((prev) =>
        prev.map((c) =>
          c.stockId === constituent.stockId ? { ...c, trackingStatus: 'Tracked' } : c,
        ),
      );
    } catch (err) {
      void messageApi.error(getErrMsg(err, 'Ошибка добавления в отслеживаемые'));
    } finally {
      setTrackingId(null);
    }
  };

  const handleFetchLivePrice = useCallback(async (constituent: IndexConstituentDto) => {
    if (!constituent.ticker?.trim()) return;
    if (!beginConstituentQuoteRefresh(quoteRefreshInFlightRef.current, constituent.stockId)) return;
    setLivePrices((prev) => ({
      ...prev,
      [constituent.stockId]: preserveEntry(prev[constituent.stockId], true),
    }));
    let quote: StockQuoteResponse;
    try {
      quote = (await getStockPrice(
        constituent.ticker,
        constituent.exchange,
        constituent.finanzenNetSlug,
      )).data;
      setLivePrices((prev) => ({
        ...prev,
        [constituent.stockId]: { quote, loading: false },
      }));
    } catch {
      setLivePrices((prev) => ({
        ...prev,
        [constituent.stockId]: preserveEntry(prev[constituent.stockId], false),
      }));
      void messageApi.error(`Ошибка получения цены для ${constituent.ticker}`);
      finishConstituentQuoteRefresh(quoteRefreshInFlightRef.current, constituent.stockId);
      return;
    }

    try {
      const { persisted, warningMessage } = await persistFreshConstituentQuote({
        constituent,
        quote,
        persistQuote: persistIndexQuote,
      });

      if (persisted != null) {
        setConstituents((prev) =>
          prev.map((item) =>
            item.stockId === constituent.stockId
              ? applyPersistedQuoteSnapshot(item, persisted)
              : item,
          ),
        );
      } else if (warningMessage) {
        void messageApi.warning(warningMessage);
      }
    } catch {
      void messageApi.error(`${QUOTE_PERSIST_FAILURE_MESSAGE} для ${constituent.ticker}`);
    } finally {
      finishConstituentQuoteRefresh(quoteRefreshInFlightRef.current, constituent.stockId);
    }
  }, [messageApi, persistIndexQuote]);

  useEffect(() => {
    setHistoryRefreshStates({});
  }, [indexId]);

  const loadConstituentHistory = useCallback(async ({
    stockId,
    range,
  }: {
    stockId: number;
    range: StockHistoryRange;
  }) => {
    const response = await getIndexConstituentHistory(indexId, stockId, range);
    return response.data;
  }, [indexId]);

  const constituentHistoryRefreshJobAdapter = useMemo(() => ({
    startJob: async (targetIndexId: number, targetStockId: number) => (
      await refreshIndexConstituentHistory(targetIndexId, targetStockId)
    ).data,
    getJobStatus: async (targetIndexId: number, targetStockId: number, jobId: string) => (
      await getIndexConstituentHistoryRefreshJob(targetIndexId, targetStockId, jobId)
    ).data,
    pollIntervalMs: INDEX_HISTORY_JOB_POLL_INTERVAL_MS,
    timeoutMs: INDEX_HISTORY_JOB_POLL_TIMEOUT_MS,
  }), []);

  const handleChartHistoryRefreshStateChange = useCallback((stockId: number, state: IndexConstituentHistoryRefreshJobState | null) => {
    setHistoryRefreshStates((prev) => {
      if (state == null) {
        if (!(stockId in prev)) return prev;
        const next = { ...prev };
        delete next[stockId];
        return next;
      }

      return { ...prev, [stockId]: state };
    });
  }, []);

  const handleBatchRefreshHistory = async () => {
    if (batchHistoryRefreshing || constituents.length === 0) {
      return;
    }

    setBatchHistoryRefreshing(true);
    setBatchHistorySummary(null);
    try {
      const response = await refreshIndexConstituentsHistory(indexId);
      const summary = formatBatchHistorySummary(response.data);
      setBatchHistorySummary(summary);

      if (response.data.failed > 0 || response.data.rateLimited > 0 || response.data.skippedRateLimited > 0) {
        void messageApi.warning(summary);
      } else {
        void messageApi.success(summary);
      }

      if (expandedStockId != null) {
        setChartRefreshTokens((prev) => ({
          ...prev,
          [expandedStockId]: (prev[expandedStockId] ?? 0) + 1,
        }));
      }
    } catch (err) {
      const text = getErrMsg(err, 'Ошибка пакетного обновления исторических данных');
      setBatchHistorySummary(text);
      void messageApi.error(text);
    } finally {
      setBatchHistoryRefreshing(false);
    }
  };

  // Abort batch quote refresh on unmount or index change
  useEffect(() => {
    return () => {
      batchQuoteAbortRef.current?.abort();
    };
  }, [indexId]);

  const handleBatchRefreshQuotes = async () => {
    if (batchQuoteRefreshing || constituents.length === 0) return;

    setBatchQuoteRefreshing(true);
    setBatchQuoteProgress(null);
    setBatchQuoteRetryWaitText(null);
    setBatchQuoteSummary(null);
    const abort = new AbortController();
    batchQuoteAbortRef.current = abort;

    try {
      const notice = await runIndexConstituentsBatchQuoteRefreshJob({
        indexId,
        startJob: async (id) => (await startIndexConstituentsBatchQuoteRefresh(id)).data,
        getJobStatus: async (id, jobId) =>
          (await getIndexConstituentsBatchQuoteRefreshJob(id, jobId)).data,
        onProgress: (processed, total) => {
          setBatchQuoteProgress({ processed, total });
        },
        onRetryWaitText: (text) => {
          setBatchQuoteRetryWaitText(text);
        },
        onInfo: (text) => { void messageApi.info(text); },
        pollIntervalMs: INDEX_BATCH_QUOTE_JOB_POLL_INTERVAL_MS,
        timeoutMs: INDEX_BATCH_QUOTE_JOB_POLL_TIMEOUT_MS,
        signal: abort.signal,
      });

      if (notice == null) {
        // Aborted — no summary
        return;
      }

      const summaryText = notice.text;
      setBatchQuoteSummary({ text: summaryText, level: notice.level });
      if (notice.level === 'success') void messageApi.success(summaryText);
      else if (notice.level === 'warning') void messageApi.warning(summaryText);
      else void messageApi.error(summaryText);

      // Reload constituents so table and expanded chart reflect authoritative DB state
      await loadData(true);
    } catch (err) {
      const text = getErrMsg(err, 'Ошибка пакетного обновления цен');
      setBatchQuoteSummary({ text, level: 'error' });
      void messageApi.error(text);
    } finally {
      setBatchQuoteRefreshing(false);
      setBatchQuoteProgress(null);
      setBatchQuoteRetryWaitText(null);
      if (batchQuoteAbortRef.current === abort) {
        batchQuoteAbortRef.current = null;
      }
    }
  };

  const filteredConstituents = useMemo(() => {
    const sorted = isStockHistoryRangeSortMode(sortMode)
      ? sortConstituentsByPerformance(constituents, performanceMap)
      : sortConstituentsByName(constituents);
    const query = search.trim().toLowerCase();
    if (!query) return sorted;
    return sorted.filter((c) =>
      c.ticker.toLowerCase().includes(query)
      || c.name.toLowerCase().includes(query)
      || (c.commonName?.toLowerCase().includes(query) ?? false)
      || (c.wkn?.toLowerCase().includes(query) ?? false)
      || (c.isin?.toLowerCase().includes(query) ?? false)
      || (c.providerSymbol?.toLowerCase().includes(query) ?? false));
  }, [constituents, search, performanceMap, sortMode]);
  const selectedSnapshotByStockId = useMemo(() => {
    const map = new Map<number, ReturnType<typeof resolveNewestCurrentPriceSnapshot>>();
    for (const constituent of constituents) {
      map.set(
        constituent.stockId,
        resolveNewestCurrentPriceSnapshot(constituent, livePrices[constituent.stockId]?.quote ?? null),
      );
    }
    return map;
  }, [constituents, livePrices]);
  const getSelectedSnapshot = useCallback((record: IndexConstituentDto) => {
    const fromMap = selectedSnapshotByStockId.get(record.stockId);
    if (fromMap) return fromMap;
    return resolveNewestCurrentPriceSnapshot(record, livePrices[record.stockId]?.quote ?? null);
  }, [livePrices, selectedSnapshotByStockId]);

  const rows = useMemo(
    () => makeConstituentRows(filteredConstituents, expandedStockId),
    [expandedStockId, filteredConstituents],
  );

  const columns: ColumnsType<TableRow> = [
    {
      title: 'Тикер',
      key: 'ticker',
      width: TICKER_COL_WIDTH,
      render: (_, record) => {
        if (isChartRow(record)) {
          const stock = constituents.find((item) => item.stockId === record._stockId);
          const live = livePrices[record._stockId];
          return {
            children: (
              <StockPriceChart
                panelId={`chart-panel-${record._stockId}`}
                stockId={record._stockId}
                indexId={indexId}
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
                refreshToken={chartRefreshTokens[record._stockId] ?? 0}
                historyLoader={loadConstituentHistory}
                historyRefreshJobAdapter={constituentHistoryRefreshJobAdapter}
                onIndexHistoryRefreshStateChange={handleChartHistoryRefreshStateChange}
              />
            ),
            props: { colSpan: INDEX_CONSTITUENTS_TOTAL_COLS },
          };
        }

        const isExpanded = expandedStockId === record.stockId;
        return (
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0, whiteSpace: 'nowrap' }}>
            <Tooltip title={record.ticker}>
              <button
                type="button"
                onClick={() =>
                  setExpandedStockId((prev) => (prev === record.stockId ? null : record.stockId))}
                aria-expanded={isExpanded}
                aria-controls={`chart-panel-${record.stockId}`}
                aria-label={
                  isExpanded
                    ? `Закрыть график цены: ${record.ticker}`
                    : `Открыть график цены: ${record.ticker}`
                }
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
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {record.ticker}
                </span>
              </button>
            </Tooltip>
            <StockExchangeTag exchange={record.exchange} />
          </div>
        );
      },
    },
    {
      title: 'Название',
      dataIndex: 'name',
      key: 'name',
      width: NAME_COL_WIDTH,
      render: (name: string, record) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        return (
          <div style={{ display: 'flex', flexDirection: 'column', width: '100%', minWidth: 0 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, width: '100%', minWidth: 0 }}>
              <Text style={{ minWidth: 0, flex: '1 1 auto', fontSize: 16 }} ellipsis={{ tooltip: name }}>{name}</Text>
              <StockClassificationBadges
                sector={record.sector ?? null}
                industry={record.industry ?? null}
              />
            </div>
            {record.commonName && record.commonName !== name && (
              <Text type="secondary" style={{ fontSize: 16 }}>{record.commonName}</Text>
            )}
            {record.isin && (
              <Text type="secondary" style={{ fontSize: 16 }}>{record.isin}</Text>
            )}
          </div>
        );
      },
    },
    {
      title: 'Текущая цена',
      key: 'savedPrice',
      align: 'right',
      width: SAVED_PRICE_COL_WIDTH,
      render: (_: unknown, record) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const selectedSnapshot = getSelectedSnapshot(record);
        return (
          <span style={{ whiteSpace: 'nowrap', fontWeight: 500 }}>
            {fmtCur(selectedSnapshot.currentPrice, '€')}
          </span>
        );
      },
    },
    {
      title: 'Изменение (€)',
      key: 'changeEur',
      width: CHANGE_EUR_COL_WIDTH,
      className: 'stock-change-compact-col',
      align: 'right',
      render: (_: unknown, record) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const selectedSnapshot = getSelectedSnapshot(record);
        const change = selectedSnapshot.currentPriceChange ?? null;
        const color = change == null
          ? '#8c8c8c'
          : change > 0
            ? COLOR_POSITIVE
            : change < 0
              ? COLOR_NEGATIVE
              : '#8c8c8c';
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
      className: 'stock-change-compact-col',
      render: (_: unknown, record) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const selectedSnapshot = getSelectedSnapshot(record);
        const pct = selectedSnapshot.currentPriceChangePercent ?? null;
        const color = pct == null
          ? '#8c8c8c'
          : pct > 0
            ? COLOR_POSITIVE
            : pct < 0
              ? COLOR_NEGATIVE
              : '#8c8c8c';
        return <span style={{ color, whiteSpace: 'nowrap' }}>{formatPercent(pct)}</span>;
      },
    },
    {
      title: 'Рост за период',
      key: 'performance',
      align: 'right',
      width: PERFORMANCE_COL_WIDTH,
      render: (_: unknown, record) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        if (performanceLoading) {
          return <span style={{ color: '#8c8c8c', fontSize: 16 }}>…</span>;
        }
        const perf = formatPerformance(performanceMap.get(record.stockId));
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
    {
      title: 'Цена API',
      key: 'apiPrice',
      align: 'right',
      width: API_PRICE_COL_WIDTH,
      className: 'stock-api-area-compact-col',
      render: (_: unknown, record) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const live = livePrices[record.stockId];
        const quote = live?.quote ?? null;
        const status = getMarketStatus(live);
        const delayed = isQuoteDelayed(quote);
        const delayTooltip = (delayed && quote?.delayWarning) ? quote.delayWarning : undefined;
        const noEurTooltip =
          !delayed && quote != null && quote.currentPriceEur == null
            ? quote.conversionWarning ?? QUOTE_NO_EUR_MESSAGE
            : undefined;
        return (
          <span title={getApiPriceTooltip(quote)} style={{ fontSize: 16, color: '#595959', whiteSpace: 'nowrap', display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            {getApiPriceText(live)}
            {delayed ? (
              <Tooltip title={delayTooltip}>
                <Tag
                  color="orange"
                  style={{ fontSize: 16, lineHeight: '22px', padding: '0 3px', marginInlineEnd: 0 }}
                  aria-label={delayTooltip ?? STALE_DELAY_LABEL}
                >
                  {STALE_DELAY_LABEL}
                </Tag>
              </Tooltip>
            ) : (
              <>
                {noEurTooltip && (
                  <Tooltip title={noEurTooltip}>
                    <Tag
                      color="gold"
                      style={{ fontSize: 16, lineHeight: '22px', padding: '0 3px', marginInlineEnd: 0 }}
                      aria-label={noEurTooltip}
                    >
                      Нет EUR
                    </Tag>
                  </Tooltip>
                )}
                {status === 'open' && (
                  <Tag color="green" style={{ fontSize: 16, lineHeight: '22px', padding: '0 3px', marginInlineEnd: 0 }}>Open</Tag>
                )}
                {status === 'closed' && (
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
      className: 'stock-api-area-compact-col',
      render: (_: unknown, record) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const selectedSnapshot = getSelectedSnapshot(record);
        const ts = selectedSnapshot.currentPriceAt ?? null;
        if (!ts) return <span style={{ whiteSpace: 'nowrap' }}>—</span>;
        return <span style={{ whiteSpace: 'nowrap' }}>{dayjs.utc(ts).local().format(PRICE_TIME_FORMAT)}</span>;
      },
    },
    {
      title: 'Действия',
      key: 'actions',
      width: ACTIONS_COL_WIDTH,
      className: 'stock-api-area-compact-col',
      render: (_, record) => {
        if (isChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const trackingLoading = trackingId === record.stockId;
        const trackButtonState = getTrackButtonState(record.trackingStatus, trackingLoading);

        const addButton = (
          <Button
            size="small"
            icon={<PlusOutlined />}
            aria-label={trackButtonState.ariaLabel}
            loading={trackButtonState.loading}
            disabled={trackButtonState.disabled}
            onClick={() => void handleTrack(record)}
          />
        );

        return (
          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
            <Button
              icon={<ReloadOutlined />}
              size="small"
              loading={livePrices[record.stockId]?.loading}
              disabled={!record.ticker?.trim() || batchHistoryRefreshing}
              onClick={() => void handleFetchLivePrice(record)}
              aria-label={`Обновить цену ${record.ticker}`}
            />
            <Tooltip title={FUNDAMENTALS_ARIA_LABEL}>
              <Button
                icon={<FundOutlined />}
                size="small"
                aria-label={FUNDAMENTALS_ARIA_LABEL}
                onClick={() => setFundamentalsStock({ id: record.stockId, ticker: record.ticker, name: record.name })}
              />
            </Tooltip>
            <Tooltip title="Редактировать акцию">
              <Button
                icon={<EditOutlined />}
                size="small"
                aria-label="Редактировать акцию"
                loading={editingStockId === record.stockId}
                onClick={() => void handleOpenEdit(record)}
              />
            </Tooltip>
            {trackButtonState.isTracked ? (
              <Tooltip title={trackButtonState.tooltip}>
                <span>{addButton}</span>
              </Tooltip>
            ) : (
              <Tooltip title={trackButtonState.tooltip}>
                {addButton}
              </Tooltip>
            )}
          </div>
        );
      },
    },
  ];

  return (
    <div style={{ padding: '8px 0' }}>
      {contextHolder}

      {batchHistorySummary && (
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 12 }}
          message={batchHistorySummary}
        />
      )}

      {batchQuoteSummary && !batchQuoteRefreshing && (
        <Alert
          type={batchQuoteSummary.level}
          showIcon
          style={{ marginBottom: 12 }}
          message={batchQuoteSummary.text}
        />
      )}

      {performanceError && !performanceLoading && (
        <Alert
          type="warning"
          showIcon
          closable
          style={{ marginBottom: 12 }}
          message={performanceError}
        />
      )}

      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
        <Input
          placeholder="Поиск по тикеру, названию, WKN, ISIN…"
          prefix={<SearchOutlined />}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          allowClear
          style={{ maxWidth: 320 }}
          size="small"
        />
        <Space size={4} align="center">
          <span style={{ fontSize: 16, color: '#595959', whiteSpace: 'nowrap' }}>Сортировка:</span>
          <Select<ConstituentSortMode>
            size="small"
            value={sortMode}
            onChange={handleSortModeChange}
            options={CONSTITUENT_SORT_MODE_OPTIONS}
            style={{ width: 90 }}
            aria-label="Сортировка"
            loading={performanceLoading}
          />
        </Space>
        {!isArchived && (
          <Button
            size="small"
            icon={<ReloadOutlined />}
            loading={refreshing}
            disabled={batchHistoryRefreshing}
            onClick={() => void handleRefresh()}
          >
            Обновить состав
          </Button>
        )}
        <Button
          size="small"
          icon={<BarChartOutlined />}
          loading={batchHistoryRefreshing}
          disabled={
            isArchived
            || constituents.length === 0
            || refreshing
            || batchHistoryRefreshing
            || Object.values(historyRefreshStates).some((state) => state === 'Queued' || state === 'Running')
          }
          onClick={() => {
            if (!window.confirm('Обновить историю всех акций текущего состава? Запросы выполняются последовательно и могут занять время.')) {
              return;
            }
            void handleBatchRefreshHistory();
          }}
        >
          Обновить историю акций
        </Button>
        <Button
          size="small"
          icon={<SyncOutlined />}
          loading={batchQuoteRefreshing}
          disabled={
            isArchived
            || constituents.length === 0
            || batchQuoteRefreshing
          }
          aria-label="Обновить текущие цены всех компонентов индекса"
          onClick={() => { void handleBatchRefreshQuotes(); }}
        >
          {batchQuoteRefreshing && batchQuoteProgress != null
            ? `Обновление цен: ${batchQuoteProgress.processed} из ${batchQuoteProgress.total}`
            : 'Обновить текущие цены'}
        </Button>
        {batchQuoteRefreshing && batchQuoteRetryWaitText && (
          <Text type="secondary" style={{ fontSize: 16 }}>
            {batchQuoteRetryWaitText}
          </Text>
        )}
      </div>

      {(sourceMeta.source || sourceMeta.asOfDate) && (
        <Alert
          type={sourceMeta.isStale ? 'warning' : 'info'}
          showIcon
          style={{ marginBottom: 12 }}
          message={(
            <Space wrap size={8}>
              <Text style={{ fontSize: 16 }}>
                Источник: {sourceMeta.source ?? '—'}
                {sourceMeta.isCuratedSnapshot ? ' (Проверенный снимок)' : ''}
              </Text>
              {sourceMeta.asOfDate && (
                <Text style={{ fontSize: 16 }}>
                  As of: {new Date(sourceMeta.asOfDate).toLocaleDateString('ru-RU')}
                </Text>
              )}
            </Space>
          )}
        />
      )}

      {loading ? (
        <div style={{ padding: '24px 0', textAlign: 'center' }}>
          <Spin />
        </div>
      ) : error ? (
        <Alert type="error" message={error} showIcon />
      ) : constituents.length === 0 ? (
        <Empty
          description="Состав этого индекса не загружен. Нажмите «Обновить состав» для импорта."
          image={Empty.PRESENTED_IMAGE_SIMPLE}
        />
      ) : (
        <Table<TableRow>
          className="stocks-table"
          rowKey={getConstituentTableRowKey}
          columns={columns}
          dataSource={rows}
          tableLayout="fixed"
          scroll={{ x: TABLE_SCROLL_X }}
          rowClassName={(record) => (isChartRow(record) ? 'chart-panel-row' : '')}
          size="small"
          pagination={{ pageSize: 20, showSizeChanger: false, hideOnSinglePage: true }}
          locale={{ emptyText: 'Нет компонентов, соответствующих поиску' }}
        />
      )}

      <StockFundamentalsDrawer
        stock={fundamentalsStock}
        open={fundamentalsStock != null}
        onClose={() => setFundamentalsStock(null)}
      />
      <StockEditModal
        open={editModalOpen}
        mode="edit"
        stock={editingStock}
        sectors={sectors}
        marketIndices={marketIndices}
        loading={editModalLoading}
        submitting={editSubmitting}
        onCancel={handleEditCancel}
        onSubmit={handleEditSubmit}
      />
    </div>
  );
};

export default IndexConstituentsPanel;
