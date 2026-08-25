import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import { Segmented, Spin, Typography, Empty, Alert, Button, Popconfirm, message } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
import {
  LineChart,
  Line,
  CartesianGrid,
  XAxis,
  YAxis,
  Tooltip as RechartsTooltip,
  ResponsiveContainer,
} from 'recharts';
import { getMarketIndexHistory, refreshMarketIndexHistory } from '../services/api';
import type { MarketIndexHistoryRange, MarketIndexHistoryPoint } from '../types';
import { MARKET_INDEX_HISTORY_RANGE_OPTIONS, toMarketIndexHistoryRange } from './historyRangeOptions';
import {
  compressIntradaySessionGaps,
  formatHistoryTimestamp,
  resolveTimestampMsForDisplayX,
} from './stockPriceChartData';

dayjs.extend(utc);

const { Text } = Typography;

const COLOR_POSITIVE = '#389e0d';
const COLOR_NEGATIVE = '#cf1322';
const COLOR_PRIMARY = '#1677ff';
const COLOR_SECONDARY_TEXT = '#8c8c8c';

const xAxisFormatByRange: Record<MarketIndexHistoryRange, string> = {
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

const SHORT_INTRADAY_GAP_MS = 2 * 60 * 60 * 1000;

type ChartPoint = {
  timestampMs: number;
  displayX?: number;
  timestamp: string;
  closeChart: number | null;
  open: number;
  high: number;
  low: number;
  volume: number | null;
  isGapMarker?: boolean;
  chartIndex?: number;
};

type CurrentSnapshot = {
  price: number | null;
  timestampUtc: string | null;
  isDelayed: boolean;
};

export function buildChartData(
  points: MarketIndexHistoryPoint[],
  range: MarketIndexHistoryRange,
  currentSnapshot?: CurrentSnapshot | null,
): ChartPoint[] {
  const sorted: ChartPoint[] = points
    .map((p) => ({
      timestampMs: dayjs.utc(p.timestamp).valueOf(),
      timestamp: p.timestamp,
      closeChart: p.close,
      open: p.open,
      high: p.high,
      low: p.low,
      volume: p.volume,
    }))
    .sort((a, b) => a.timestampMs - b.timestampMs);

  if (range === '1y' && sorted.length > 0) {
    const latest = sorted[sorted.length - 1];
    const overlayPrice = currentSnapshot?.price ?? null;
    const overlayTimestamp = currentSnapshot?.timestampUtc ?? null;
    const overlayTimestampMs = overlayTimestamp ? dayjs.utc(overlayTimestamp).valueOf() : Number.NaN;

    if (
      currentSnapshot?.isDelayed !== true
      && Number.isFinite(overlayTimestampMs)
      && overlayTimestampMs <= Date.now()
      && typeof overlayPrice === 'number'
      && Number.isFinite(overlayPrice)
      && overlayPrice > 0
      && overlayTimestampMs > latest.timestampMs
    ) {
      sorted.push({
        timestampMs: overlayTimestampMs,
        timestamp: overlayTimestamp!,
        closeChart: overlayPrice,
        open: overlayPrice,
        high: overlayPrice,
        low: overlayPrice,
        volume: null,
      });
    }
  }

  if (range === '1w') {
    return sorted.map((pt, idx) => ({ ...pt, chartIndex: idx }));
  }

  if (range !== '24h' && range !== 'today') {
    return sorted;
  }

  // Intraday gap handling
  if (sorted.length < 2) return sorted;
  const result: ChartPoint[] = [sorted[0]];
  let prev = sorted[0];
  for (let i = 1; i < sorted.length; i++) {
    const cur = sorted[i];
    if (cur.timestampMs - prev.timestampMs > SHORT_INTRADAY_GAP_MS) {
      result.push({
        ...prev,
        timestampMs: prev.timestampMs + 1,
        timestamp: new Date(prev.timestampMs + 1).toISOString(),
        closeChart: null,
        isGapMarker: true,
      });
    }
    result.push(cur);
    prev = cur;
  }
  return result;
}

const formatNumber = (v: number, digits = 2): string =>
  new Intl.NumberFormat('ru-RU', { maximumFractionDigits: digits }).format(v);

const formatIndexPoints = (v: number): string => `${formatNumber(v, 2)} пт`;

const formatSigned = (v: number, digits = 2): string =>
  `${v >= 0 ? '+' : ''}${formatNumber(v, digits)}`;

export interface MarketIndexPriceChartProps {
  panelId: string;
  indexId: number;
  code: string;
  name: string;
  providerSymbol?: string | null;
  isArchived?: boolean;
}

const MarketIndexPriceChart: React.FC<MarketIndexPriceChartProps> = ({
  panelId,
  indexId,
  code,
  name,
  providerSymbol,
  isArchived,
}) => {
  const [range, setRange] = useState<MarketIndexHistoryRange>('1y');
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [points, setPoints] = useState<MarketIndexHistoryPoint[]>([]);
  const [currentSnapshot, setCurrentSnapshot] = useState<CurrentSnapshot | null>(null);
  const [isStale, setIsStale] = useState(false);
  const [staleReason, setStaleReason] = useState<string | null>(null);
  const [messageApi, contextHolder] = message.useMessage();
  const chartLayoutRef = useRef<HTMLDivElement | null>(null);
  const [chartLayoutWidth, setChartLayoutWidth] = useState(0);

  const hasProviderSymbol = !!providerSymbol;

  const fetchHistory = useCallback(async () => {
    if (!hasProviderSymbol) return;
    setLoading(true);
    setErrorMsg(null);
    try {
      const res = await getMarketIndexHistory(indexId, range);
      setPoints(res.data.points);
      setCurrentSnapshot({
        price: res.data.currentPrice ?? null,
        timestampUtc: res.data.currentPriceAt ?? null,
        isDelayed: res.data.currentPriceIsDelayed === true,
      });
      setIsStale(res.data.isStale);
      setStaleReason(res.data.staleReason ?? null);
    } catch {
      setErrorMsg('Ошибка загрузки исторических данных');
      setPoints([]);
      setCurrentSnapshot(null);
    } finally {
      setLoading(false);
    }
  }, [hasProviderSymbol, indexId, range]);

  useEffect(() => {
    void fetchHistory();
  }, [fetchHistory]);

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

  const handleRefresh = useCallback(async () => {
    if (refreshing) return;
    setRefreshing(true);
    try {
      const res = await refreshMarketIndexHistory(indexId);
      await fetchHistory();
      const { deletedPoints, importedPoints } = res.data;
      messageApi.success(`История перезагружена: удалено ${deletedPoints}, загружено ${importedPoints}`);
    } catch (err: unknown) {
      const msg =
        err != null && typeof err === 'object' && 'response' in err &&
        err.response != null && typeof err.response === 'object' && 'data' in err.response &&
        typeof (err.response as { data: unknown }).data === 'string'
          ? (err.response as { data: string }).data
          : 'Не удалось перезагрузить историю';
      messageApi.error(msg);
    } finally {
      setRefreshing(false);
    }
  }, [fetchHistory, indexId, messageApi, refreshing]);

  const chartData = useMemo(
    () => buildChartData(points, range, currentSnapshot),
    [currentSnapshot, points, range],
  );
  const displayChartData = useMemo(
    () => range === '24h'
      ? compressIntradaySessionGaps(chartData, chartLayoutWidth)
      : chartData,
    [chartData, chartLayoutWidth, range],
  );
  const resolveCompressedTs = useCallback(
    (displayX: number) => resolveTimestampMsForDisplayX(displayChartData, displayX),
    [displayChartData],
  );

  const weeklyIndexToMs = useMemo(() => {
    const map = new Map<number, number>();
    if (range === '1w') {
      displayChartData.forEach((pt) => {
        if (pt.chartIndex !== undefined) map.set(pt.chartIndex, pt.timestampMs);
      });
    }
    return map;
  }, [range, displayChartData]);

  const resolveWeeklyTs = useCallback(
    (idx: number) => weeklyIndexToMs.get(Math.round(idx)),
    [weeklyIndexToMs],
  );

  const firstClose = useMemo(() => {
    for (const pt of displayChartData) {
      if (pt.closeChart != null) return pt.closeChart;
    }
    return null;
  }, [displayChartData]);

  const latestClose = useMemo(() => {
    for (let i = displayChartData.length - 1; i >= 0; i--) {
      if (displayChartData[i].closeChart != null) return displayChartData[i].closeChart;
    }
    return null;
  }, [displayChartData]);

  const changeValue = latestClose != null && firstClose != null ? latestClose - firstClose : null;
  const changePercent = changeValue != null && firstClose != null && firstClose !== 0
    ? (changeValue / firstClose) * 100 : null;
  const perfColor = changeValue == null ? undefined : changeValue >= 0 ? COLOR_POSITIVE : COLOR_NEGATIVE;

  const hasVolume = points.some((p) => p.volume != null && p.volume > 0);

  const renderXAxis = (hide = false) => (
    range === '1w' ? (
      <XAxis
        hide={hide}
        type="number"
        dataKey="chartIndex"
        scale="linear"
        domain={['dataMin', 'dataMax']}
        tick={{ fontSize: 16 }}
        tickFormatter={(idx: number) => {
          const ts = resolveWeeklyTs(idx);
          return ts != null ? dayjs.utc(ts).local().format(xAxisFormatByRange['1w']) : '';
        }}
      />
    ) : (
      range === '24h' ? (
        <XAxis
          hide={hide}
          type="number"
          dataKey="displayX"
          scale="linear"
          domain={['dataMin', 'dataMax']}
          tick={{ fontSize: 16 }}
          tickFormatter={(value: number) => {
            const ts = resolveCompressedTs(value);
            return ts != null ? formatHistoryTimestamp(ts, range, xAxisFormatByRange[range]) : '';
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
        tickFormatter={(value: number) => dayjs.utc(value).local().format(xAxisFormatByRange[range])}
      />
      )
    )
  );

  if (!hasProviderSymbol) {
    return (
      <div id={panelId} style={{ padding: '16px 20px', background: '#f0f7ff', borderBottom: '3px solid #1677ff' }}>
        <Empty description="Символ поставщика не указан для этого индекса. Укажите ProviderSymbol в настройках индекса для загрузки графика." />
      </div>
    );
  }

  return (
    <>
      {contextHolder}
      <div
        id={panelId}
        style={{ padding: '16px 20px', background: '#f0f7ff', borderBottom: '3px solid #1677ff' }}
      >
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12, flexWrap: 'wrap', gap: 8 }}>
          <Text strong style={{ fontSize: 16 }}>
            История: {code} — {name}
          </Text>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            {isArchived && (
              <Text type="secondary" style={{ fontSize: 16 }}>Архивный индекс</Text>
            )}
            <Popconfirm
              title="Перезагрузить историю?"
              description="Все сохранённые исторические данные будут удалены и загружены заново."
              okText="Перезагрузить"
              cancelText="Отмена"
              onConfirm={() => void handleRefresh()}
              disabled={refreshing || isArchived}
            >
              <Button
                size="small"
                icon={<ReloadOutlined />}
                loading={refreshing}
                disabled={refreshing || isArchived}
                title={isArchived ? 'Архивный индекс: обновление заблокировано' : undefined}
              >
                Перезагрузить
              </Button>
            </Popconfirm>
          </div>
        </div>

        <Segmented
          value={range}
          onChange={(value) => setRange(toMarketIndexHistoryRange(value, range))}
          options={MARKET_INDEX_HISTORY_RANGE_OPTIONS}
          style={{ marginBottom: 12 }}
        />

        {isStale && staleReason && (
          <Alert type="warning" showIcon message={staleReason} style={{ marginBottom: 10 }} />
        )}

        {loading ? (
          <div style={{ textAlign: 'center', padding: 32 }}><Spin /></div>
        ) : errorMsg ? (
          <Alert type="error" message={errorMsg} style={{ margin: '12px 0' }} />
        ) : displayChartData.length === 0 ? (
          <Empty description="Нет исторических данных для выбранного диапазона" style={{ margin: '12px 0' }} />
        ) : (
          <>
            {/* Summary row */}
            <div style={{ display: 'flex', gap: 24, flexWrap: 'wrap', marginBottom: 10, alignItems: 'baseline' }}>
              <Text style={{ fontSize: 20, fontWeight: 700 }}>
                {latestClose != null ? formatIndexPoints(latestClose) : '—'}
              </Text>
              {changeValue != null && changePercent != null && (
                <Text style={{ fontSize: 16, color: perfColor, fontWeight: 600 }}>
                  {formatSigned(changeValue, 2)} пт ({formatSigned(changePercent, 2)}%)
                </Text>
              )}
              <Text type="secondary" style={{ fontSize: 16, marginLeft: 'auto' }}>
                От начала выбранного периода
              </Text>
            </div>

            {/* Price chart */}
            <div ref={chartLayoutRef}>
            <ResponsiveContainer width="100%" height={220}>
              <LineChart data={displayChartData} margin={{ top: 4, right: 4, left: 0, bottom: 0 }}>
                <CartesianGrid strokeDasharray="3 3" vertical={false} />
                {renderXAxis()}
                <YAxis
                  domain={['auto', 'auto']}
                  tick={{ fontSize: 16 }}
                  tickFormatter={(v: number) => formatNumber(v, 0)}
                  width={64}
                />
                <RechartsTooltip
                  content={({ active, payload }) => {
                    if (!active || !payload?.length) return null;
                    const pt = payload[0]?.payload as ChartPoint | undefined;
                    if (!pt) return null;
                    const ts = range === '1w' && pt.chartIndex !== undefined
                      ? resolveWeeklyTs(pt.chartIndex) ?? pt.timestampMs
                      : pt.timestampMs;
                    const fmt = xAxisFormatByRange[range];
                    return (
                      <div style={{ background: '#fff', border: '1px solid #d9d9d9', borderRadius: 4, padding: '8px 12px', fontSize: 16 }}>
                        <div style={{ fontWeight: 600, marginBottom: 4 }}>{dayjs.utc(ts).local().format(`DD.MM.YYYY${fmt.includes('HH') ? ' HH:mm' : ''}`)}</div>
                        {pt.closeChart != null && <div>Закрытие: <b>{formatIndexPoints(pt.closeChart)}</b></div>}
                        {pt.open > 0 && <div style={{ color: COLOR_SECONDARY_TEXT }}>О: {formatNumber(pt.open)} В: {formatNumber(pt.high)} Н: {formatNumber(pt.low)}</div>}
                        {hasVolume && pt.volume != null && <div style={{ color: COLOR_SECONDARY_TEXT }}>Объём: {formatNumber(pt.volume, 0)}</div>}
                      </div>
                    );
                  }}
                />
                <Line
                  type="monotone"
                  dataKey="closeChart"
                  stroke={perfColor ?? COLOR_PRIMARY}
                  dot={false}
                  strokeWidth={1.5}
                  connectNulls={false}
                />
              </LineChart>
            </ResponsiveContainer>
            </div>

            {/* Provider note */}
            <Text type="secondary" style={{ fontSize: 16 }}>
              Источник: Yahoo Finance · {providerSymbol} · Данные могут быть задержаны
            </Text>
          </>
        )}
      </div>
    </>
  );
};

export default MarketIndexPriceChart;
