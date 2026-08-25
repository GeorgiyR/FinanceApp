import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { Segmented, Spin, Typography, Empty, Alert, Button, Tooltip, message, Popconfirm, Modal, Input } from 'antd';
import { LinkOutlined, ReloadOutlined } from '@ant-design/icons';
import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
import {
  Bar,
  BarChart,
  Cell,
  LineChart,
  Line,
  CartesianGrid,
  XAxis,
  YAxis,
  Tooltip as RechartsTooltip,
  ResponsiveContainer,
} from 'recharts';
import {
  getIndexConstituentHistory,
  getStockHistory,
  getStockHistoryRoutingDiagnostics,
  hardResetStockHistory,
  refreshStockHistory,
  validateStockHistoryProviderSymbol,
} from '../services/api';
import {
  getStockPriceChartSummary,
} from './stockPriceChartSummary';
import {
  buildHistoryChartData,
  compressIntradaySessionGaps,
  formatHistoryTimestamp,
  resolveTimestampMsForDisplayX,
  usesUtcDateLabels,
} from './stockPriceChartData';
import type { HistoryChartPoint } from './stockPriceChartData';
import {
  analyzeAdaptiveVolumeScale,
  formatVolumeTooltipValue,
  getVolumeCadenceHint,
  toDisplayVolume,
} from './stockVolumeScale';
import { resolveVolumeBarSize } from './stockVolumeBarSize';
import { buildFinanzenNetUrl } from '../utils/finanzenNet';
import { resolveNewestCurrentPriceSnapshot } from '../utils/currentPriceSnapshot';
import {
  DAY_HIGH_LOW_VALUE_FONT_SIZE,
  CURRENT_PRICE_FONT_SIZE,
  getDayHighLowDisplay,
  getDayRangeLabel,
} from './dayHighLow';
import { STOCK_HISTORY_RANGE_OPTIONS, toStockHistoryRange } from './historyRangeOptions';
import type {
  IndexConstituentHistoryRefreshJobResponse,
  IndexConstituentHistoryRefreshJobState,
  StockHistoryRange,
  StockHistoryRepairDiagnosticsResponse,
  StockHistoryResponse,
  StockQuoteResponse,
} from '../types';
import {
  getHistoryRefreshErrorMessage,
  runIndexConstituentHistoryRefreshJob,
} from './indexConstituentHistoryRefresh';
import StockTechnicalAnalysisPanel from './StockTechnicalAnalysisPanel';
import './StockPriceChart.css';

dayjs.extend(utc);

const { Text } = Typography;

const COLOR_POSITIVE = '#389e0d';
const COLOR_NEGATIVE = '#cf1322';
const COLOR_PRIMARY = '#1677ff';
const COLOR_SECONDARY_TEXT = '#8c8c8c';
const COLOR_VOLUME = '#4096ff';
export const RANGE_BOUND_COLOR = COLOR_SECONDARY_TEXT;
export const DAY_RANGE_ARROW_TEXT = ' → ';
export const BASELINE_BLOCK_STYLE = { marginLeft: 'auto', textAlign: 'right' } as const;
export const PERIOD_CHANGE_HEADING = 'Изменение от начала периода';

const xAxisFormatByRange: Record<StockHistoryRange, string> = {
  '5y': 'MM.YYYY',
  '3y': 'MM.YYYY',
  '1y': 'DD.MM.YY',
  '6m': 'DD.MM.YY',
  '3m': 'DD.MM.YY',
  '1m': 'DD',
  '1w': 'DD.MM HH:mm',
  '24h': 'HH:mm',
  today: 'HH:mm',
};

export interface StockPriceChartProps {
  panelId: string;
  stockId: number;
  indexId?: number;
  ticker: string;
  name: string;
  exchange?: string | null;
  providerSymbol?: string | null;
  wkn?: string | null;
  isin?: string | null;
  finanzenNetSlug?: string | null;
  liveQuote?: StockQuoteResponse | null;
  storedPriceEur?: number | null;
  storedPriceChangeEur?: number | null;
  storedPriceTimestampUtc?: string | null;
  refreshToken?: number;
  historyLoader?: (params: {
    stockId: number;
    range: StockHistoryRange;
    indexId?: number;
  }) => Promise<StockHistoryResponse>;
  historyRefreshJobAdapter?: {
    startJob: (indexId: number, stockId: number) => Promise<IndexConstituentHistoryRefreshJobResponse>;
    getJobStatus: (
      indexId: number,
      stockId: number,
      jobId: string,
    ) => Promise<IndexConstituentHistoryRefreshJobResponse>;
    pollIntervalMs?: number;
    timeoutMs?: number;
  };
  onIndexHistoryRefreshStateChange?: (
    stockId: number,
    state: IndexConstituentHistoryRefreshJobState | null,
  ) => void;
}

const formatSigned = (value: number, suffix = '') =>
  `${value >= 0 ? '+' : ''}${value.toFixed(2)}${suffix}`;

const formatCurrencyValue = (value: number, currencyCode: string | null | undefined): string => {
  if ((currencyCode ?? 'EUR') === 'EUR') {
    return `€${value.toFixed(2)}`;
  }

  return `${value.toFixed(2)} ${currencyCode ?? '—'}`;
};

const formatRawQuote = (quote: StockQuoteResponse): string =>
  `${quote.rawCurrentPrice.toFixed(2)} ${quote.currency ?? quote.normalizedQuoteCurrency ?? '—'}`;

const formatNumber = (value: number, maximumFractionDigits = 2): string =>
  new Intl.NumberFormat('ru-RU', { maximumFractionDigits }).format(value);

const formatCompactNumber = (value: number): string =>
  new Intl.NumberFormat('ru-RU', { notation: 'compact', maximumFractionDigits: 1 }).format(value);

const resolveExpectedProviderSymbol = (
  ticker: string,
  exchange?: string | null,
  providerSymbol?: string | null,
): string | null => {
  if (providerSymbol?.trim()) {
    return providerSymbol.trim();
  }
  if (!ticker.trim()) {
    return null;
  }
  if (exchange?.trim().toLowerCase() === 'frankfurt' && !ticker.includes('.')) {
    return `${ticker}.F`;
  }
  return ticker;
};

const StockPriceChart: React.FC<StockPriceChartProps> = ({
  panelId,
  stockId,
  indexId,
  ticker,
  name,
  exchange,
  providerSymbol,
  wkn,
  isin,
  finanzenNetSlug,
  liveQuote,
  storedPriceEur,
  storedPriceChangeEur,
  storedPriceTimestampUtc,
  refreshToken,
  historyLoader,
  historyRefreshJobAdapter,
  onIndexHistoryRefreshStateChange,
}) => {
  const [historyRange, setHistoryRange] = useState<StockHistoryRange>('1y');
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyRefreshing, setHistoryRefreshing] = useState(false);
  const [historyResponse, setHistoryResponse] = useState<StockHistoryResponse | null>(null);
  const [hardResetModalOpen, setHardResetModalOpen] = useState(false);
  const [hardResetLoading, setHardResetLoading] = useState(false);
  const [validationLoading, setValidationLoading] = useState(false);
  const [providerSymbolCandidate, setProviderSymbolCandidate] = useState('');
  const [hardResetConfirmation, setHardResetConfirmation] = useState('');
  const [repairDiagnostics, setRepairDiagnostics] = useState<StockHistoryRepairDiagnosticsResponse | null>(null);
  const [repairValidation, setRepairValidation] = useState<StockHistoryRepairDiagnosticsResponse | null>(null);
  const historyRefreshAbortRef = useRef<AbortController | null>(null);
  const historyRefreshStateChangeRef = useRef(onIndexHistoryRefreshStateChange);
  const chartLayoutRef = useRef<HTMLDivElement | null>(null);
  const [chartLayoutWidth, setChartLayoutWidth] = useState(0);

  useEffect(() => {
    historyRefreshStateChangeRef.current = onIndexHistoryRefreshStateChange;
  }, [onIndexHistoryRefreshStateChange]);

  const notifyIndexHistoryRefreshStateChange = useCallback((state: IndexConstituentHistoryRefreshJobState | null) => {
    historyRefreshStateChangeRef.current?.(stockId, state);
  }, [stockId]);

  const finanzenNetUrl = useMemo(() => buildFinanzenNetUrl(finanzenNetSlug), [finanzenNetSlug]);

  const fetchHistory = useCallback(async () => {
    setHistoryLoading(true);
    try {
      if (historyLoader) {
        const response = await historyLoader({ stockId, range: historyRange, indexId });
        setHistoryResponse(response);
      } else if (indexId != null) {
        const response = await getIndexConstituentHistory(indexId, stockId, historyRange);
        setHistoryResponse(response.data);
      } else {
        const response = await getStockHistory(stockId, historyRange);
        setHistoryResponse(response.data);
      }
    } catch {
      setHistoryResponse(null);
      message.error('Ошибка загрузки исторических данных');
    } finally {
      setHistoryLoading(false);
    }
  }, [historyLoader, historyRange, indexId, stockId]);

  useEffect(() => {
    fetchHistory();
  }, [fetchHistory, refreshToken]);

  useEffect(() => {
    const container = chartLayoutRef.current;
    if (container == null) {
      return undefined;
    }

    const updateWidth = () => {
      const width = container.getBoundingClientRect().width;
      setChartLayoutWidth((previousWidth) =>
        Math.abs(previousWidth - width) < 0.5 ? previousWidth : width);
    };

    updateWidth();

    if (typeof ResizeObserver === 'undefined') {
      window.addEventListener('resize', updateWidth);
      return () => window.removeEventListener('resize', updateWidth);
    }

    const observer = new ResizeObserver(updateWidth);
    observer.observe(container);
    return () => observer.disconnect();
  }, []);

  const handleRefreshHistory = useCallback(async () => {
    if (historyRefreshing) {
      return;
    }

    historyRefreshAbortRef.current?.abort();
    const abortController = new AbortController();
    historyRefreshAbortRef.current = abortController;
    setHistoryRefreshing(true);
    try {
      if (indexId != null && historyRefreshJobAdapter != null) {
        const notice = await runIndexConstituentHistoryRefreshJob({
          indexId,
          stockId,
          ticker,
          startJob: historyRefreshJobAdapter.startJob,
          getJobStatus: historyRefreshJobAdapter.getJobStatus,
          pollIntervalMs: historyRefreshJobAdapter.pollIntervalMs,
          timeoutMs: historyRefreshJobAdapter.timeoutMs,
          signal: abortController.signal,
          onInfo: (text) => { void message.info(text); },
          onStateChange: notifyIndexHistoryRefreshStateChange,
        });

        if (notice != null) {
          if (notice.level === 'success') {
            void message.success(notice.text);
          } else if (notice.level === 'warning') {
            void message.warning(notice.text);
          } else if (notice.level === 'info') {
            void message.info(notice.text);
          } else {
            void message.error(notice.text);
          }

          if (notice.refreshChart) {
            await fetchHistory();
          }
        }
      } else {
        const refreshRes = await refreshStockHistory(stockId);
        await fetchHistory();
        const { deletedPoints, importedPoints } = refreshRes.data;
        message.success(`История перезагружена: удалено ${deletedPoints}, загружено ${importedPoints}`);
      }
    } catch (error: unknown) {
      if (!abortController.signal.aborted) {
        message.error(getHistoryRefreshErrorMessage(error, `Ошибка запуска обновления исторических данных для ${ticker}`));
      }
    } finally {
      notifyIndexHistoryRefreshStateChange(null);
      if (historyRefreshAbortRef.current === abortController) {
        historyRefreshAbortRef.current = null;
      }
      setHistoryRefreshing(false);
    }
  }, [
    fetchHistory,
    historyRefreshJobAdapter,
    historyRefreshing,
    indexId,
    notifyIndexHistoryRefreshStateChange,
    stockId,
    ticker,
  ]);

  const openHardResetModal = useCallback(async () => {
    setHardResetModalOpen(true);
    setRepairValidation(null);
    setHardResetConfirmation('');
    setValidationLoading(true);
    try {
      const response = await getStockHistoryRoutingDiagnostics(stockId);
      setRepairDiagnostics(response.data);
      setProviderSymbolCandidate(response.data.configuredProviderSymbol ?? '');
    } catch (error: unknown) {
      setRepairDiagnostics(null);
      message.error(getHistoryRefreshErrorMessage(error, 'Не удалось загрузить диагностику маршрутизации истории.'));
    } finally {
      setValidationLoading(false);
    }
  }, [stockId]);

  const handleValidateProviderSymbol = useCallback(async () => {
    setValidationLoading(true);
    try {
      const response = await validateStockHistoryProviderSymbol(stockId, providerSymbolCandidate || null);
      setRepairValidation(response.data);
      if (response.data.resultBucket === 'success' || response.data.resultBucket === 'partial') {
        message.success('Символ поставщика успешно проверен.');
      } else {
        message.warning('Проверка завершилась с предупреждениями или ошибками. Сброс не будет выполнен.');
      }
    } catch (error: unknown) {
      message.error(getHistoryRefreshErrorMessage(error, 'Не удалось проверить символ поставщика.'));
    } finally {
      setValidationLoading(false);
    }
  }, [providerSymbolCandidate, stockId]);

  const handleHardReset = useCallback(async () => {
    setHardResetLoading(true);
    try {
      const response = await hardResetStockHistory(stockId, hardResetConfirmation, providerSymbolCandidate || null);
      setRepairValidation(response.data);
      if (!response.data.resetPerformed) {
        message.warning('Полный сброс не выполнен. Проверьте диагностику и повторите попытку.');
        return;
      }

      message.success(`История полностью восстановлена: удалено ${response.data.deletedRows}, вставлено ${response.data.insertedRows}.`);
      setHistoryResponse(null);
      await fetchHistory();
      setHardResetModalOpen(false);
    } catch (error: unknown) {
      message.error(getHistoryRefreshErrorMessage(error, 'Не удалось выполнить полный сброс истории.'));
    } finally {
      setHardResetLoading(false);
    }
  }, [fetchHistory, hardResetConfirmation, providerSymbolCandidate, stockId]);

  useEffect(() => () => {
    historyRefreshAbortRef.current?.abort();
    historyRefreshAbortRef.current = null;
    notifyIndexHistoryRefreshStateChange(null);
  }, [notifyIndexHistoryRefreshStateChange]);

  useEffect(() => {
    historyRefreshAbortRef.current?.abort();
    historyRefreshAbortRef.current = null;
    notifyIndexHistoryRefreshStateChange(null);
  }, [indexId, notifyIndexHistoryRefreshStateChange, stockId]);

  const historyData = historyResponse?.points ?? [];
  const hasQuoteDerivedPoints = useMemo(
    () => historyData.some((point) => point.isQuoteDerived === true),
    [historyData],
  );
  const currentSessionHasNoCandles = (historyRange === '24h' || historyRange === 'today')
    && historyResponse?.currentSessionHasCandles === false;
  const expectedProviderSymbol = useMemo(
    () => resolveExpectedProviderSymbol(ticker, exchange, providerSymbol),
    [exchange, providerSymbol, ticker],
  );
  const quoteMatchesListing = useMemo(() => {
    if (!expectedProviderSymbol || !liveQuote?.symbol) {
      return true;
    }
    return expectedProviderSymbol.toUpperCase() === liveQuote.symbol.toUpperCase();
  }, [expectedProviderSymbol, liveQuote?.symbol]);
  const listingLiveQuote = quoteMatchesListing ? liveQuote : null;
  const hardResetConfirmationValid = useMemo(() => {
    const normalized = hardResetConfirmation.trim().toUpperCase();
    return normalized === 'УДАЛИТЬ' || normalized === ticker.trim().toUpperCase();
  }, [hardResetConfirmation, ticker]);
  const hardResetValidationPassed = repairValidation?.resultBucket === 'success' || repairValidation?.resultBucket === 'partial';
  const historyHasEurConversion = historyResponse?.rateToEur != null;
  const historyCurrencyCode = historyHasEurConversion
    ? 'EUR'
    : historyResponse?.normalizedQuoteCurrency ?? historyResponse?.currency ?? null;
  const volumeMetrics = historyResponse?.volumeMetrics ?? null;
  const baseHistoryChartData = useMemo(() => buildHistoryChartData(historyData, historyRange), [historyData, historyRange]);

  const firstHistoryClose = useMemo(() => {
    for (const point of baseHistoryChartData) {
      if (point.closeChart != null) return point.closeChart;
    }
    return null;
  }, [baseHistoryChartData]);

  const latestHistoryClose = useMemo(() => {
    for (let i = baseHistoryChartData.length - 1; i >= 0; i -= 1) {
      if (baseHistoryChartData[i].closeChart != null) return baseHistoryChartData[i].closeChart;
    }
    return null;
  }, [baseHistoryChartData]);

  const displayCurrencyCode = historyCurrencyCode ?? listingLiveQuote?.normalizedQuoteCurrency ?? listingLiveQuote?.currency ?? 'EUR';
  const selectedSessionSnapshot = useMemo(
    () => resolveNewestCurrentPriceSnapshot(
      {
        currentPrice: storedPriceEur,
        currentPriceChange: storedPriceChangeEur,
        currentPriceAt: storedPriceTimestampUtc,
      },
      listingLiveQuote,
    ),
    [listingLiveQuote, storedPriceChangeEur, storedPriceEur, storedPriceTimestampUtc],
  );

  const currentPriceDisplayValue = useMemo(() => {
    if (historyHasEurConversion) {
      return selectedSessionSnapshot.currentPrice ?? latestHistoryClose;
    }

    if (listingLiveQuote?.normalizedCurrentPrice != null) return listingLiveQuote.normalizedCurrentPrice;
    return latestHistoryClose;
  }, [historyHasEurConversion, latestHistoryClose, listingLiveQuote, selectedSessionSnapshot.currentPrice]);

  const currentPriceDisplayText = useMemo(() => {
    if (historyHasEurConversion && selectedSessionSnapshot.currentPrice != null) {
      return formatCurrencyValue(selectedSessionSnapshot.currentPrice, 'EUR');
    }

    if (!historyHasEurConversion && listingLiveQuote != null) {
      return formatRawQuote(listingLiveQuote);
    }

    if (currentPriceDisplayValue == null) {
      return '—';
    }

    return formatCurrencyValue(currentPriceDisplayValue, displayCurrencyCode);
  }, [
    currentPriceDisplayValue,
    displayCurrencyCode,
    historyHasEurConversion,
    listingLiveQuote,
    selectedSessionSnapshot.currentPrice,
  ]);

  const currentQuoteOverlay = useMemo(() => {
    if (
      historyHasEurConversion
      && selectedSessionSnapshot.currentPriceAt
      && selectedSessionSnapshot.currentPrice != null
    ) {
      return {
        timestampUtc: selectedSessionSnapshot.currentPriceAt,
        closeChart: selectedSessionSnapshot.currentPrice,
        rawClose: selectedSessionSnapshot.source === 'live'
          ? (selectedSessionSnapshot.liveQuote?.rawCurrentPrice ?? selectedSessionSnapshot.currentPrice)
          : selectedSessionSnapshot.currentPrice,
        isStale: selectedSessionSnapshot.isDelayed,
        isForRequestedInstrument: selectedSessionSnapshot.source !== 'live' || quoteMatchesListing,
      };
    }

    if (
      !historyHasEurConversion
      && listingLiveQuote?.isStale !== true
      && listingLiveQuote?.priceTimestampUtc
      && currentPriceDisplayValue != null
    ) {
      return {
        timestampUtc: listingLiveQuote.priceTimestampUtc,
        closeChart: currentPriceDisplayValue,
        rawClose: listingLiveQuote.rawCurrentPrice,
        isForRequestedInstrument: quoteMatchesListing,
      };
    }

    return null;
  }, [
    currentPriceDisplayValue,
    historyHasEurConversion,
    listingLiveQuote,
    quoteMatchesListing,
    selectedSessionSnapshot,
  ]);

  const historyChartData = useMemo(
    () => currentQuoteOverlay == null
      ? baseHistoryChartData
      : buildHistoryChartData(historyData, historyRange, currentQuoteOverlay),
    [baseHistoryChartData, currentQuoteOverlay, historyData, historyRange],
  );

  const volumeScale = useMemo(
    () => analyzeAdaptiveVolumeScale(
      exchange,
      historyChartData.map((point) => point.volumeChart),
      { historyRange, interval: historyResponse?.interval },
    ),
    [exchange, historyChartData, historyRange, historyResponse?.interval],
  );

  const historyChartDataWithVolumeDisplay = useMemo(
    () => historyChartData.map((point) => {
      const { displayVolume, volumeCapped } = toDisplayVolume(point.volumeChart, volumeScale);
      return {
        ...point,
        volumeDisplay: displayVolume,
        volumeCapped,
      };
    }),
    [historyChartData, volumeScale],
  );

  const displayHistoryChartData = useMemo(
    () => historyRange === '24h'
      ? compressIntradaySessionGaps(historyChartDataWithVolumeDisplay, chartLayoutWidth)
      : historyChartDataWithVolumeDisplay,
    [chartLayoutWidth, historyChartDataWithVolumeDisplay, historyRange],
  );

  const resolveCompressedTs = useCallback(
    (displayX: number) => resolveTimestampMsForDisplayX(displayHistoryChartData, displayX),
    [displayHistoryChartData],
  );
  const positiveRenderableVolumePoints = useMemo(
    () => displayHistoryChartData.reduce(
      (count, point) => (point.volumeDisplay != null && Number.isFinite(point.volumeDisplay) && point.volumeDisplay > 0
        ? count + 1
        : count),
      0,
    ),
    [displayHistoryChartData],
  );
  const volumeBarSize = useMemo(
    () => resolveVolumeBarSize({
      containerWidth: chartLayoutWidth,
      positivePointCount: positiveRenderableVolumePoints,
      historyRange,
    }),
    [chartLayoutWidth, historyRange, positiveRenderableVolumePoints],
  );

  const weeklyIndexToTimestampMs = useMemo(() => {
    const map = new Map<number, number>();
    if (historyRange === '1w') {
      displayHistoryChartData.forEach((pt) => {
        if (pt.chartIndex !== undefined) {
          map.set(pt.chartIndex, pt.timestampMs);
        }
      });
    }
    return map;
  }, [displayHistoryChartData, historyRange]);

  const resolveWeeklyTs = useCallback(
    (idx: number) => weeklyIndexToTimestampMs.get(Math.round(idx)),
    [weeklyIndexToTimestampMs],
  );

  const periodSummary = useMemo(
    () => getStockPriceChartSummary({
      historyRange,
      currentPriceDisplayValue,
      firstHistoryClose,
      historyHasEurConversion,
      liveQuote: selectedSessionSnapshot.source === 'live' ? selectedSessionSnapshot.liveQuote : null,
      storedPriceEur: selectedSessionSnapshot.source === 'persisted'
        ? selectedSessionSnapshot.currentPrice
        : null,
      storedPriceChangeEur: selectedSessionSnapshot.source === 'persisted'
        ? selectedSessionSnapshot.currentPriceChange
        : null,
    }),
    [
      currentPriceDisplayValue,
      firstHistoryClose,
      historyHasEurConversion,
      historyRange,
      selectedSessionSnapshot,
    ],
  );
  const periodChangeValue = periodSummary.changeValue;
  const periodChangePercent = periodSummary.changePercent;
  const performanceColor =
    periodChangeValue == null
      ? undefined
      : periodChangeValue >= 0
        ? COLOR_POSITIVE
        : COLOR_NEGATIVE;

  const warningText = listingLiveQuote?.conversionWarning ?? historyResponse?.conversionWarning ?? null;
  const normalizedQuoteText =
    listingLiveQuote != null && listingLiveQuote.quoteUnitMultiplier !== 1 && listingLiveQuote.normalizedQuoteCurrency
      ? `${listingLiveQuote.normalizedCurrentPrice.toFixed(3)} ${listingLiveQuote.normalizedQuoteCurrency}`
      : null;

  const dayHighLowDisplay = useMemo(
    () => getDayHighLowDisplay(listingLiveQuote, historyData, historyHasEurConversion),
    [listingLiveQuote, historyData, historyHasEurConversion],
  );
  const latestVolumePoint = useMemo(() => {
    if (!volumeMetrics?.latestMetricsTimestamp) {
      return null;
    }

    return historyData.find((point) => point.timestamp === volumeMetrics.latestMetricsTimestamp) ?? null;
  }, [historyData, volumeMetrics?.latestMetricsTimestamp]);
  const volumeMetricItems = useMemo(() => [
    {
      key: 'volume',
      label: 'Объём',
      tooltip: volumeMetrics?.usesCompletedCandle
        ? 'Объём последней завершённой свечи в выбранном диапазоне.'
        : 'Объём последней доступной свечи в выбранном диапазоне.',
      value: latestVolumePoint == null ? '—' : formatNumber(latestVolumePoint.volume, 0),
    },
    {
      key: 'averageVolume20',
      label: 'Ø20',
      tooltip: 'Средний объём за последние 20 свечей выбранного диапазона.',
      value: volumeMetrics?.averageVolume20 == null ? '—' : formatNumber(volumeMetrics.averageVolume20),
    },
    {
      key: 'averageVolume50',
      label: 'Ø50',
      tooltip: 'Средний объём за последние 50 свечей выбранного диапазона.',
      value: volumeMetrics?.averageVolume50 == null ? '—' : formatNumber(volumeMetrics.averageVolume50),
    },
    {
      key: 'relativeVolume',
      label: 'RVOL',
      tooltip: 'Относительный объём = последний объём / средний объём за 20 периодов.',
      value: volumeMetrics?.relativeVolume == null ? '—' : `${volumeMetrics.relativeVolume.toFixed(2)}x`,
    },
    {
      key: 'turnover',
      label: 'Оборот',
      tooltip: 'Цена закрытия × объём. Использует ту же валютную нормализацию, что и график цены.',
      value: volumeMetrics?.turnover == null
        ? '—'
        : formatCurrencyValue(volumeMetrics.turnover, volumeMetrics.turnoverCurrency),
    },
  ], [latestVolumePoint, volumeMetrics]);
  const volumeCadenceHint = useMemo(
    () => getVolumeCadenceHint(historyRange, historyResponse?.interval),
    [historyRange, historyResponse?.interval],
  );
  const isRobustFrankfurtLongRangeMode = volumeScale.activationReason === 'deterministicFrankfurtLongRange';
  const renderXAxis = (hide = false) => (
    historyRange === '1w' ? (
      <XAxis
        hide={hide}
        type="number"
        dataKey="chartIndex"
        scale="linear"
        domain={['dataMin', 'dataMax']}
        tick={{ fontSize: 16 }}
        tickFormatter={(idx: number) => {
          const ts = resolveWeeklyTs(idx);
          return ts != null
            ? formatHistoryTimestamp(ts, '1w', xAxisFormatByRange['1w'])
            : '';
        }}
      />
    ) : historyRange === '24h' ? (
      <XAxis
        hide={hide}
        type="number"
        dataKey="displayX"
        scale="linear"
        domain={['dataMin', 'dataMax']}
        tick={{ fontSize: 16 }}
        tickFormatter={(value: number) => {
          const ts = resolveCompressedTs(value);
          return ts == null
            ? ''
            : formatHistoryTimestamp(ts, historyRange, xAxisFormatByRange[historyRange]);
        }}
      />
    ) : (
      <XAxis
        hide={hide}
        type="number"
        dataKey="timestampMs"
        scale="time"
        domain={['dataMin', 'dataMax']}
        tick={{ fontSize: 16 }}
        tickFormatter={(value: number) =>
          formatHistoryTimestamp(value, historyRange, xAxisFormatByRange[historyRange])
        }
      />
    )
  );

  const renderDayRangeBound = (
    entry: typeof dayHighLowDisplay.minimum,
    currencyCode: string,
  ) => {
    if (entry.value == null) {
      return <span style={{ color: RANGE_BOUND_COLOR }}>—</span>;
    }

    const formatted = formatCurrencyValue(entry.value, currencyCode);

    const rawValue = entry.rawValue;
    const rawValueDiffers = rawValue != null && Math.abs(rawValue - entry.value) > 1e-9;
    const rawValueText =
      rawValueDiffers && historyResponse?.currency != null
        ? `Исходное значение: ${rawValue.toFixed(2)} ${historyResponse.currency}`
        : null;
    const timestampText =
      entry.timestampUtc != null
        ? `${entry.isFromLiveQuote ? 'Время котировки' : 'Свеча'}: ${formatHistoryTimestamp(
          entry.timestampUtc,
          historyRange,
          usesUtcDateLabels(historyRange) ? 'DD.MM.YYYY' : 'DD.MM.YYYY HH:mm',
        )}`
        : null;

    const tooltip = [timestampText, rawValueText].filter((value): value is string => value != null).join('\n');

    return tooltip !== '' ? (
      <Tooltip title={<span style={{ whiteSpace: 'pre-line' }}>{tooltip}</span>}>
        <span style={{ color: RANGE_BOUND_COLOR, cursor: 'help' }}>{formatted}</span>
      </Tooltip>
    ) : (
      <span style={{ color: RANGE_BOUND_COLOR }}>{formatted}</span>
    );
  };

  return (
    <div
      id={panelId}
      style={{
        padding: '16px 20px',
        background: '#f0f7ff',
        borderBottom: '3px solid #1677ff',
      }}
    >
      <div
        style={{
          display: 'flex',
          gap: 24,
          flexWrap: 'wrap',
          marginBottom: 10,
          padding: '6px 0',
        }}
      >
        <span>
          <Text type="secondary" style={{ fontSize: 16 }}>WKN: </Text>
          <Text style={{ fontSize: 16, fontWeight: 600, fontFamily: 'monospace', color: '#1677ff' }}>
            {wkn ?? '—'}
          </Text>
        </span>
        <span>
          <Text type="secondary" style={{ fontSize: 16 }}>ISIN: </Text>
          <Text style={{ fontSize: 16, fontWeight: 600, fontFamily: 'monospace', color: '#1677ff' }}>
            {isin ?? '—'}
          </Text>
        </span>
        <span>
          <Text type="secondary" style={{ fontSize: 16 }}>Валюта котировки: </Text>
          <Text style={{ fontSize: 16, fontWeight: 600 }}>
            {listingLiveQuote?.currency ?? historyResponse?.currency ?? '—'}
          </Text>
        </span>
        <span>
          <Text type="secondary" style={{ fontSize: 16 }}>Валюта отчётности: </Text>
          <Text style={{ fontSize: 16, fontWeight: 600 }}>
            {listingLiveQuote?.financialCurrency ?? historyResponse?.financialCurrency ?? '—'}
          </Text>
        </span>
      </div>
      {warningText && (
        <Alert
          type="warning"
          showIcon
          message={warningText}
          style={{ marginBottom: 12 }}
        />
      )}
      {historyResponse?.isPotentiallyStale && historyResponse.staleReason && (
        <Alert
          type="warning"
          showIcon
          message={historyResponse.staleReason}
          style={{ marginBottom: 12 }}
        />
      )}
      {currentSessionHasNoCandles && (
        <Alert
          type="info"
          showIcon
          message="Текущая торговая сессия ещё не содержит свечей — показана предыдущая завершённая сессия."
          style={{ marginBottom: 12 }}
        />
      )}
      {hasQuoteDerivedPoints && (
        <Alert
          type="info"
          showIcon
          message="Часть внутридневных точек получена из котировок и будет детерминированно заменена провайдерными свечами при следующей сверке истории."
          style={{ marginBottom: 12 }}
        />
      )}
      {!quoteMatchesListing && expectedProviderSymbol && (
        <Alert
          type="info"
          showIcon
          message={`Текущая котировка ${liveQuote?.symbol ?? '—'} не совпадает с выбранным листингом ${expectedProviderSymbol} и не добавляется на график.`}
          style={{ marginBottom: 12 }}
        />
      )}
      <div
        style={{
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          marginBottom: 12,
          flexWrap: 'wrap',
          gap: 8,
        }}
      >
        <Text strong style={{ fontSize: 16 }}>
          История цены: {ticker} — {name}
        </Text>
        <Text type="secondary" style={{ fontSize: 14 }}>
          Данные на: {historyResponse?.asOfUtc ? dayjs.utc(historyResponse.asOfUtc).local().format('DD.MM.YYYY HH:mm') : '—'}
        </Text>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          <Popconfirm
            title="Перезагрузить историю?"
            description="Все сохранённые исторические данные по этой акции будут удалены и загружены заново по текущему тикеру и бирже."
            okText="Перезагрузить"
            cancelText="Отмена"
            onConfirm={handleRefreshHistory}
            disabled={historyRefreshing}
          >
            <Button
              size="small"
              icon={<ReloadOutlined />}
              loading={historyRefreshing}
              disabled={historyRefreshing}
            >
              Перезагрузить историю
            </Button>
          </Popconfirm>
          <Button
            size="small"
            danger
            onClick={() => void openHardResetModal()}
            disabled={historyRefreshing || hardResetLoading || validationLoading}
          >
            Полностью восстановить историю
          </Button>
          <Segmented
            className="stock-price-chart-segmented"
            value={historyRange}
            onChange={(value) => setHistoryRange(toStockHistoryRange(value, historyRange))}
            options={STOCK_HISTORY_RANGE_OPTIONS}
          />
        </div>
      </div>
      {historyLoading ? (
        <div style={{ display: 'flex', justifyContent: 'center', padding: 40 }}>
          <Spin />
        </div>
      ) : historyData.length === 0 ? (
        <Empty description={historyResponse?.unavailableReason ?? 'Нет данных для выбранного периода'} />
      ) : (
        <div style={{ display: 'grid', gap: 12 }}>
          <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
            <div
              style={{
                minWidth: 240,
                padding: '8px 12px',
                border: '1px solid #d0e8ff',
                borderRadius: 8,
                background: '#fff',
              }}
            >
              <div className="stock-price-chart-summary-header">
                <Text type="secondary" style={{ fontSize: 16 }}>
                  Изменение за период (к текущей цене)
                </Text>
                <Tooltip
                  title={
                    finanzenNetUrl
                      ? 'Открыть страницу инструмента на finanzen.net'
                      : 'Для открытия finanzen.net укажите finanzen.net Slug в настройках акции'
                  }
                >
                  <Button
                    type="link"
                    size="small"
                    icon={<LinkOutlined />}
                    disabled={!finanzenNetUrl}
                    aria-label="Открыть на finanzen.net"
                    style={{ padding: '0 4px', height: 'auto', fontSize: 16 }}
                    onClick={() => {
                      if (!finanzenNetUrl) return;
                      const popup = window.open(
                        finanzenNetUrl,
                        'finanzen_net_popup',
                        'noopener,noreferrer,width=1200,height=800,resizable=yes,scrollbars=yes',
                      );
                      if (popup) {
                        popup.opener = null;
                      }
                    }}
                  >
                    finanzen.net
                  </Button>
                </Tooltip>
              </div>
              <div
                style={{
                  display: 'flex',
                  gap: 16,
                  alignItems: 'flex-end',
                  flexWrap: 'wrap',
                  marginTop: 4,
                }}
              >
                <div>
                  <div style={{ fontSize: 16, color: COLOR_SECONDARY_TEXT, marginBottom: 2 }}>
                    Тек. цена
                  </div>
                  <div style={{ color: COLOR_PRIMARY, fontSize: CURRENT_PRICE_FONT_SIZE, fontWeight: 600 }}>
                    {currentPriceDisplayText}
                  </div>
                  {normalizedQuoteText && (
                    <div style={{ fontSize: 16, color: COLOR_SECONDARY_TEXT, marginTop: 2 }}>
                      Нормализовано: {normalizedQuoteText}
                    </div>
                  )}
                </div>
                <div>
                  <div style={{ fontSize: 16, color: COLOR_SECONDARY_TEXT, marginBottom: 2 }}>
                    {getDayRangeLabel(historyRange)}
                  </div>
                  <div style={{ fontSize: DAY_HIGH_LOW_VALUE_FONT_SIZE, fontWeight: 600, color: RANGE_BOUND_COLOR }}>
                    {renderDayRangeBound(dayHighLowDisplay.minimum, displayCurrencyCode)}
                    <span style={{ color: RANGE_BOUND_COLOR }}>{DAY_RANGE_ARROW_TEXT}</span>
                    {renderDayRangeBound(dayHighLowDisplay.maximum, displayCurrencyCode)}
                  </div>
                </div>
                <div style={{ color: COLOR_SECONDARY_TEXT, fontSize: 16, fontWeight: 600 }}>
                  {periodSummary.baselineValue == null
                    ? '—'
                    : formatCurrencyValue(periodSummary.baselineValue, displayCurrencyCode)}
                </div>
                <div style={BASELINE_BLOCK_STYLE}>
                  <div style={{ fontSize: 16, color: COLOR_SECONDARY_TEXT, marginBottom: 2 }}>
                    {PERIOD_CHANGE_HEADING}
                  </div>
                  <div style={{ color: performanceColor ?? 'inherit', fontWeight: 600 }}>
                    {periodChangeValue == null
                      ? '—'
                      : `${formatCurrencyValue(periodChangeValue, displayCurrencyCode)} (${periodChangePercent == null ? '—' : formatSigned(periodChangePercent, '%')})`}
                  </div>
                </div>
              </div>
            </div>
            <div
              style={{
                minWidth: 280,
                flex: '1 1 320px',
                padding: '8px 12px',
                border: '1px solid #d0e8ff',
                borderRadius: 8,
                background: '#fff',
              }}
            >
              <div className="stock-price-chart-summary-header">
                <Text type="secondary" style={{ fontSize: 16 }}>
                  Объём и активность торгов
                </Text>
              </div>
              {volumeCadenceHint != null && (
                <div style={{ marginTop: 4 }}>
                  <Text type="secondary" style={{ fontSize: 16 }}>
                    {volumeCadenceHint}
                  </Text>
                </div>
              )}
              {volumeScale.adaptiveScaleActive && (
                <div style={{ marginTop: 6 }}>
                  <Text type="secondary" style={{ fontSize: 16 }}>
                    {isRobustFrankfurtLongRangeMode
                      ? 'Робастная шкала объёма: для долгого диапазона Frankfurt крупные выбросы визуально ограничены.'
                      : 'Адаптивная шкала объёма: крупные выбросы визуально ограничены.'}
                  </Text>
                </div>
              )}
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: 'repeat(auto-fit, minmax(96px, 1fr))',
                  gap: 10,
                  marginTop: 8,
                }}
              >
                {volumeMetricItems.map((item) => (
                  <div key={item.key}>
                    <Tooltip title={item.tooltip}>
                      <div style={{ fontSize: 16, color: COLOR_SECONDARY_TEXT, marginBottom: 2, cursor: 'help' }}>
                        {item.label}
                      </div>
                    </Tooltip>
                    <div style={{ fontSize: 16, fontWeight: 600 }}>
                      {item.value}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </div>
          <div ref={chartLayoutRef}>
          <div style={{ width: '100%', height: 240 }}>
            <ResponsiveContainer>
              <LineChart data={displayHistoryChartData} syncId={`stock-history-${stockId}`}>
                <CartesianGrid strokeDasharray="3 3" />
                {renderXAxis()}
                <YAxis
                  domain={['auto', 'auto']}
                  tick={{ fontSize: 16 }}
                  tickFormatter={(value: number) =>
                    formatCurrencyValue(value, displayCurrencyCode)
                  }
                />
                <RechartsTooltip
                  contentStyle={{ fontSize: 16 }}
                  itemStyle={{ fontSize: 16 }}
                  labelStyle={{ fontSize: 16 }}
                  labelFormatter={(value: number) => {
                    if (historyRange === '1w') {
                      const ts = resolveWeeklyTs(value);
                      return ts != null ? formatHistoryTimestamp(ts, '1w', 'DD.MM.YYYY HH:mm') : '';
                    }
                    if (historyRange === '24h') {
                      const ts = resolveCompressedTs(value);
                      return ts != null ? formatHistoryTimestamp(ts, historyRange, 'DD.MM.YYYY HH:mm') : '';
                    }
                    return formatHistoryTimestamp(
                      value,
                      historyRange,
                      usesUtcDateLabels(historyRange) ? 'DD.MM.YYYY' : 'DD.MM.YYYY HH:mm',
                    );
                  }}
                  formatter={(value: unknown, _name: string, item) => {
                    const payload = item.payload as HistoryChartPoint | undefined;
                    if (value == null || payload == null) {
                      return ['Нет данных', 'Цена'];
                    }

                    const formattedChartValue = formatCurrencyValue(Number(value), displayCurrencyCode);
                    if (historyHasEurConversion) {
                      return [formattedChartValue, 'Цена'];
                    }

                    return [
                      `${formattedChartValue} (raw: ${payload.rawClose.toFixed(2)} ${historyResponse?.currency ?? displayCurrencyCode ?? '—'})`,
                      'Цена',
                    ];
                  }}
                />
                <Line
                  type="monotone"
                  dataKey="closeChart"
                  name={`Close (${displayCurrencyCode ?? '—'})`}
                  stroke="#1677ff"
                  dot={false}
                  strokeWidth={2}
                  connectNulls={false}
                />
              </LineChart>
            </ResponsiveContainer>
          </div>
          <div style={{ width: '100%', height: 168 }}>
            {!volumeScale.hasPositiveFiniteVolume ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Поставщик не предоставил данные об объёме" />
            ) : (
              <ResponsiveContainer>
                <BarChart data={displayHistoryChartData} syncId={`stock-history-${stockId}`}>
                  <CartesianGrid strokeDasharray="3 3" vertical={false} />
                  {renderXAxis(true)}
                  {volumeScale.adaptiveScaleActive && volumeScale.displayUpperBound != null ? (
                    <YAxis
                      domain={[0, volumeScale.displayUpperBound]}
                      allowDataOverflow
                      tick={{ fontSize: 16 }}
                      tickFormatter={(value: number) => formatCompactNumber(value)}
                      width={60}
                    />
                  ) : (
                    <YAxis tick={{ fontSize: 16 }} tickFormatter={(value: number) => formatCompactNumber(value)} width={60} />
                  )}
                  <RechartsTooltip
                    contentStyle={{ fontSize: 16 }}
                    itemStyle={{ fontSize: 16 }}
                    labelStyle={{ fontSize: 16 }}
                    labelFormatter={(value: number) => {
                      if (historyRange === '1w') {
                        const ts = resolveWeeklyTs(value);
                        return ts != null ? formatHistoryTimestamp(ts, '1w', 'DD.MM.YYYY HH:mm') : '';
                      }
                      if (historyRange === '24h') {
                        const ts = resolveCompressedTs(value);
                        return ts != null ? formatHistoryTimestamp(ts, historyRange, 'DD.MM.YYYY HH:mm') : '';
                      }
                      return formatHistoryTimestamp(
                        value,
                        historyRange,
                        usesUtcDateLabels(historyRange) ? 'DD.MM.YYYY' : 'DD.MM.YYYY HH:mm',
                      );
                    }}
                    formatter={(_value: unknown, _name: string, item) => {
                      const payload = item.payload as HistoryChartPoint | undefined;
                      if (payload == null || payload.volumeChart == null || !Number.isFinite(payload.volumeChart)) {
                        return ['Нет данных', 'Объём'];
                      }

                      return [formatVolumeTooltipValue(payload.volumeChart, payload.volumeCapped === true), 'Объём'];
                    }}
                  />
                  <Bar
                    dataKey="volumeDisplay"
                    name="Объём"
                    fill={COLOR_VOLUME}
                    fillOpacity={1}
                    isAnimationActive={false}
                    barSize={volumeBarSize}
                    minPointSize={volumeScale.adaptiveScaleActive ? 3 : 1}
                  >
                    {displayHistoryChartData.map((entry, index) => (
                      <Cell
                        key={`volume-cell-${entry.timestamp}-${index}`}
                        fill={entry.volumeCapped ? '#1677ff' : COLOR_VOLUME}
                      />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            )}
          </div>
          </div>
        </div>
      )}
      <StockTechnicalAnalysisPanel stockId={stockId} />
      <Modal
        title="Сбросить и загрузить историю заново"
        open={hardResetModalOpen}
        onCancel={() => setHardResetModalOpen(false)}
        footer={[
          <Button key="cancel" onClick={() => setHardResetModalOpen(false)}>
            Отмена
          </Button>,
          <Button
            key="reset"
            danger
            type="primary"
            loading={hardResetLoading}
            disabled={!hardResetValidationPassed || !hardResetConfirmationValid || validationLoading}
            onClick={() => void handleHardReset()}
          >
            Полностью восстановить историю
          </Button>,
        ]}
      >
        <div style={{ display: 'grid', gap: 10 }}>
          <Alert
            type="warning"
            showIcon
            message="Операция разрушительная: старые исторические ряды будут удалены без сохранения."
          />
          <Text>StockId: <b>{stockId}</b> · {ticker} — {name}</Text>
          <Text type="secondary">
            ProviderSymbol: configured {repairDiagnostics?.configuredProviderSymbol ?? '—'} · effective {repairDiagnostics?.effectiveProviderSymbol ?? '—'}
          </Text>
          <Input
            value={providerSymbolCandidate}
            onChange={(event) => setProviderSymbolCandidate(event.target.value)}
            placeholder="Кандидат ProviderSymbol (например ENR.DE)"
            disabled={validationLoading || hardResetLoading}
          />
          <Button onClick={() => void handleValidateProviderSymbol()} loading={validationLoading} disabled={hardResetLoading}>
            Проверить символ
          </Button>
          {repairValidation && (
            <>
              <Alert
                type={repairValidation.resultBucket === 'success' || repairValidation.resultBucket === 'partial' ? 'info' : 'error'}
                showIcon
                message={`Результат: ${repairValidation.resultBucket}`}
              />
              <Text type="secondary">
                Удалено: {repairValidation.deletedRows} · Вставлено: {repairValidation.insertedRows} · Финал: {repairValidation.finalRows}
              </Text>
              {repairValidation.intervals.map((interval) => (
                <Text key={interval.interval} type={interval.isUnavailable ? 'warning' : undefined}>
                  {interval.interval}: fetched={interval.fetchedCount}, accepted={interval.acceptedCount}, rejected={interval.rejectedCount}
                </Text>
              ))}
              {repairValidation.warnings.map((warning, index) => (
                <Text key={`warning-${index}`} type="warning">{warning}</Text>
              ))}
              {repairValidation.errors.map((errorText, index) => (
                <Text key={`error-${index}`} type="danger">{errorText}</Text>
              ))}
            </>
          )}
          <Input
            value={hardResetConfirmation}
            onChange={(event) => setHardResetConfirmation(event.target.value)}
            placeholder={`Введите "${ticker}" или "УДАЛИТЬ"`}
            disabled={hardResetLoading}
          />
        </div>
      </Modal>
    </div>
  );
};

export default StockPriceChart;
