import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Card, Drawer, Empty, Row, Col, Space, Spin, Table, Tabs, Tag, Typography, message } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import dayjs from 'dayjs';
import utc from 'dayjs/plugin/utc';
import type { AxiosError } from 'axios';
import { Link } from 'react-router-dom';
import { getStockFundamentals, refreshStockFundamentals } from '../services/api';
import type { EarningsDateStatus, EarningsEventDto, FinancialPeriodDto, FundamentalsResponse, Stock } from '../types';

dayjs.extend(utc);

const { Text, Title } = Typography;

export const FUNDAMENTALS_EMPTY_TEXT = '—';
export const EARNINGS_STATUS_LABELS: Record<EarningsDateStatus, string> = {
  Estimated: 'Ожидаемая',
  Confirmed: 'Подтверждённая',
  Unknown: 'Статус неизвестен',
};

type BadgeConfig = {
  color: string;
  text: string;
};

export const getFundamentalsNumberDisplay = (value: number | null | undefined, fractionDigits = 2): string =>
  value == null ? FUNDAMENTALS_EMPTY_TEXT : new Intl.NumberFormat('ru-RU', { maximumFractionDigits: fractionDigits }).format(value);

export const formatCompactFinancialValue = (value: number | null | undefined, currency?: string | null): string => {
  if (value == null) {
    return FUNDAMENTALS_EMPTY_TEXT;
  }

  const absValue = Math.abs(value);
  const suffix =
    absValue >= 1_000_000_000_000 ? 'T'
      : absValue >= 1_000_000_000 ? 'B'
        : absValue >= 1_000_000 ? 'M'
          : absValue >= 1_000 ? 'K'
            : '';
  const divisor =
    suffix === 'T' ? 1_000_000_000_000
      : suffix === 'B' ? 1_000_000_000
        : suffix === 'M' ? 1_000_000
          : suffix === 'K' ? 1_000
            : 1;
  const compact = divisor === 1
    ? getFundamentalsNumberDisplay(value)
    : `${(value / divisor).toFixed(1)}${suffix}`;
  return currency ? `${compact} ${currency}` : compact;
};

export const getEarningsStatusBadgeProps = (status: EarningsDateStatus): BadgeConfig => {
  switch (status) {
    case 'Estimated':
      return { color: 'gold', text: EARNINGS_STATUS_LABELS.Estimated };
    case 'Confirmed':
      return { color: 'green', text: EARNINGS_STATUS_LABELS.Confirmed };
    default:
      return { color: 'default', text: EARNINGS_STATUS_LABELS.Unknown };
  }
};

export const canRefreshFundamentals = (stock: Pick<Stock, 'id'> | null, refreshing: boolean): boolean =>
  Boolean(stock?.id) && !refreshing;

export const getFundamentalsErrorMessage = (error: unknown): string => {
  const axiosError = error as AxiosError<string> | undefined;
  const serverMessage = axiosError?.response?.data;
  return typeof serverMessage === 'string' && serverMessage.trim().length > 0
    ? serverMessage
    : 'Не удалось загрузить фундаментальные данные';
};

export const shouldShowFundamentalsRefreshSuccess = (response: FundamentalsResponse): boolean =>
  response.state === 'Fresh' && response.snapshot !== null;

export const getFundamentalsRefreshWarningMessage = (response: FundamentalsResponse): string => {
  if (response.state === 'Stale') {
    return response.warningMessage?.trim().length
      ? response.warningMessage
      : 'Не удалось обновить фундаментальные данные. Показан сохранённый снимок.';
  }

  return response.warningMessage?.trim().length
    ? response.warningMessage
    : 'Не удалось загрузить фундаментальные данные.';
};

export const shouldDiscardFundamentalsResponse = (requestVersion: number, currentVersion: number): boolean =>
  requestVersion !== currentVersion;

export const shouldDiscardFundamentalsResponseForStock = (
  requestStockId: number,
  currentStockId: number | null | undefined,
): boolean => requestStockId !== currentStockId;

type StockSummary = Pick<Stock, 'id' | 'ticker' | 'name'>;

interface StockFundamentalsDrawerProps {
  stock: StockSummary | null;
  open: boolean;
  onClose: () => void;
}

const FINANCIAL_COLUMNS = [
  {
    title: 'Период',
    key: 'period',
    render: (_: unknown, record: FinancialPeriodDto) =>
      record.periodEndDate ? dayjs.utc(record.periodEndDate).local().format('DD.MM.YYYY') : FUNDAMENTALS_EMPTY_TEXT,
  },
  {
    title: 'Выручка',
    dataIndex: 'revenue',
    key: 'revenue',
    align: 'right' as const,
    render: (value: number | null, record: FinancialPeriodDto) => formatCompactFinancialValue(value, record.reportedCurrency),
  },
  {
    title: 'Опер. прибыль',
    dataIndex: 'operatingIncome',
    key: 'operatingIncome',
    align: 'right' as const,
    render: (value: number | null, record: FinancialPeriodDto) => formatCompactFinancialValue(value, record.reportedCurrency),
  },
  {
    title: 'Чистая прибыль',
    dataIndex: 'netIncome',
    key: 'netIncome',
    align: 'right' as const,
    render: (value: number | null, record: FinancialPeriodDto) => formatCompactFinancialValue(value, record.reportedCurrency),
  },
  {
    title: 'EBITDA',
    dataIndex: 'ebitda',
    key: 'ebitda',
    align: 'right' as const,
    render: (value: number | null, record: FinancialPeriodDto) => formatCompactFinancialValue(value, record.reportedCurrency),
  },
  {
    title: 'FCF',
    dataIndex: 'freeCashFlow',
    key: 'freeCashFlow',
    align: 'right' as const,
    render: (value: number | null, record: FinancialPeriodDto) => formatCompactFinancialValue(value, record.reportedCurrency),
  },
];

const EARNINGS_COLUMNS = [
  {
    title: 'Дата отчёта',
    key: 'date',
    render: (_: unknown, record: EarningsEventDto) => {
      const date = record.reportDate ?? record.fiscalPeriodEnd;
      if (!date) {
        return FUNDAMENTALS_EMPTY_TEXT;
      }

      const start = dayjs.utc(date).local().format('DD.MM.YYYY');
      const end = record.reportDateEnd ? dayjs.utc(record.reportDateEnd).local().format('DD.MM.YYYY') : null;
      return end && end !== start ? `${start} – ${end}` : start;
    },
  },
  {
    title: 'Конец периода',
    key: 'fiscalPeriodEnd',
    render: (_: unknown, record: EarningsEventDto) =>
      record.fiscalPeriodEnd ? dayjs.utc(record.fiscalPeriodEnd).local().format('DD.MM.YYYY') : FUNDAMENTALS_EMPTY_TEXT,
  },
  {
    title: 'Статус',
    key: 'status',
    render: (_: unknown, record: EarningsEventDto) => {
      const badge = getEarningsStatusBadgeProps(record.dateStatus);
      return <Tag color={badge.color}>{badge.text}</Tag>;
    },
  },
  {
    title: 'Период',
    dataIndex: 'fiscalPeriod',
    key: 'fiscalPeriod',
    render: (value: string | null) => value ?? FUNDAMENTALS_EMPTY_TEXT,
  },
  {
    title: 'EPS est.',
    dataIndex: 'epsEstimate',
    key: 'epsEstimate',
    align: 'right' as const,
    render: (value: number | null) => getFundamentalsNumberDisplay(value, 4),
  },
  {
    title: 'EPS rep.',
    dataIndex: 'epsReported',
    key: 'epsReported',
    align: 'right' as const,
    render: (value: number | null) => getFundamentalsNumberDisplay(value, 4),
  },
  {
    title: 'Rev. est.',
    dataIndex: 'revenueEstimate',
    key: 'revenueEstimate',
    align: 'right' as const,
    render: (value: number | null) => formatCompactFinancialValue(value),
  },
  {
    title: 'Rev. rep.',
    dataIndex: 'revenueReported',
    key: 'revenueReported',
    align: 'right' as const,
    render: (value: number | null) => formatCompactFinancialValue(value),
  },
];

const StockFundamentalsDrawer: React.FC<StockFundamentalsDrawerProps> = ({ stock, open, onClose }) => {
  const [data, setData] = useState<FundamentalsResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const requestVersionRef = useRef(0);
  const activeStockIdRef = useRef<number | null>(stock?.id ?? null);

  const loadFundamentals = useCallback(async (forceRefresh = false) => {
    if (!stock?.id) {
      return;
    }

    const requestStockId = stock.id;
    const requestVersion = ++requestVersionRef.current;
    if (forceRefresh) {
      setRefreshing(true);
    } else {
      setLoading(true);
    }

    setErrorMessage(null);
    try {
      const response = forceRefresh
        ? await refreshStockFundamentals(stock.id)
        : await getStockFundamentals(stock.id);
      if (
        shouldDiscardFundamentalsResponse(requestVersion, requestVersionRef.current) ||
        shouldDiscardFundamentalsResponseForStock(requestStockId, activeStockIdRef.current)
      ) {
        return;
      }

      setData(response.data);
      if (forceRefresh) {
        if (shouldShowFundamentalsRefreshSuccess(response.data)) {
          message.success('Фундаментальные данные обновлены');
        } else {
          message.warning(getFundamentalsRefreshWarningMessage(response.data));
        }
      }
    } catch (error) {
      if (
        shouldDiscardFundamentalsResponse(requestVersion, requestVersionRef.current) ||
        shouldDiscardFundamentalsResponseForStock(requestStockId, activeStockIdRef.current)
      ) {
        return;
      }

      const nextMessage = getFundamentalsErrorMessage(error);
      setErrorMessage(nextMessage);
      if (forceRefresh) {
        message.error(nextMessage);
      }
    } finally {
      if (
        !shouldDiscardFundamentalsResponse(requestVersion, requestVersionRef.current) &&
        !shouldDiscardFundamentalsResponseForStock(requestStockId, activeStockIdRef.current)
      ) {
        setLoading(false);
        setRefreshing(false);
      }
    }
  }, [stock?.id]);

  useEffect(() => {
    activeStockIdRef.current = stock?.id ?? null;
    requestVersionRef.current += 1;
    setData(null);
    setErrorMessage(null);
  }, [stock?.id]);

  useEffect(() => {
    if (open && stock?.id) {
      void loadFundamentals(false);
    }
  }, [loadFundamentals, open, stock?.id]);

  const snapshot = data?.snapshot ?? null;
  const annualPeriods = useMemo(
    () => (data?.periods ?? []).filter((period) => period.periodType === 'Annual'),
    [data?.periods],
  );
  const quarterlyPeriods = useMemo(
    () => (data?.periods ?? []).filter((period) => period.periodType === 'Quarterly'),
    [data?.periods],
  );

  return (
    <Drawer
      title={stock ? `${stock.ticker} — фундаментальные показатели` : 'Фундаментальные показатели'}
      width={960}
      open={open}
      onClose={onClose}
      extra={(
        <Button
          icon={<ReloadOutlined />}
          loading={refreshing}
          disabled={!canRefreshFundamentals(stock, refreshing)}
          onClick={() => { void loadFundamentals(true); }}
        >
          Обновить
        </Button>
      )}
    >
      {loading ? (
        <div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}>
          <Spin size="large" />
        </div>
      ) : errorMessage ? (
        <Alert type="error" showIcon message={errorMessage} />
      ) : !data || data.state === 'Unavailable' || !snapshot ? (
        <Empty description={data?.warningMessage ?? 'Фундаментальные данные пока недоступны'} />
      ) : (
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap' }}>
            <div>
              <Title level={5} style={{ margin: 0 }}>{stock?.name}</Title>
              <Text type="secondary">
                Источник: {snapshot.source} · символ {snapshot.sourceSymbol}
              </Text>
              <div>
                <Link to="/help/fundamentals#fundamentals-where-and-fields">
                  Как читать фундаментальные показатели
                </Link>
              </div>
            </div>
            <Space>
              <Tag color={data.state === 'Fresh' ? 'green' : 'gold'}>
                {data.state === 'Fresh' ? 'Свежие данные' : 'Устаревшие данные'}
              </Tag>
              {snapshot.asOfDate && (
                <Tag>As of {dayjs.utc(snapshot.asOfDate).local().format('DD.MM.YYYY')}</Tag>
              )}
            </Space>
          </div>

          {data.warningMessage && <Alert type="warning" showIcon message={data.warningMessage} />}

          <Row gutter={[12, 12]}>
            {[
              { key: 'marketCap', title: 'Market Cap', value: snapshot.marketCap },
              { key: 'enterpriseValue', title: 'Enterprise Value', value: snapshot.enterpriseValue },
              { key: 'totalDebt', title: 'Total Debt', value: snapshot.totalDebt },
              { key: 'cashAndEquivalents', title: 'Cash', value: snapshot.cashAndEquivalents },
              { key: 'revenueTtm', title: 'TTM Revenue', value: snapshot.revenueTtm },
              { key: 'netIncomeTtm', title: 'TTM Net Income', value: snapshot.netIncomeTtm },
              { key: 'ebitdaTtm', title: 'TTM EBITDA', value: snapshot.ebitdaTtm },
              { key: 'freeCashFlowTtm', title: 'TTM FCF', value: snapshot.freeCashFlowTtm },
            ].map((item) => (
              <Col xs={24} sm={12} lg={6} key={item.key}>
                <Card size="small">
                  <Text type="secondary">{item.title}</Text>
                  <Title level={5} style={{ margin: '8px 0 0' }}>
                    {formatCompactFinancialValue(item.value, snapshot.currency)}
                  </Title>
                </Card>
              </Col>
            ))}
          </Row>

          <Tabs
            items={[
              {
                key: 'summary',
                label: 'Мультипликаторы',
                children: (
                  <Row gutter={[12, 12]}>
                    {[
                      { key: 'peRatio', label: 'P/E', value: getFundamentalsNumberDisplay(snapshot.peRatio, 4) },
                      { key: 'forwardPeRatio', label: 'Forward P/E', value: getFundamentalsNumberDisplay(snapshot.forwardPeRatio, 4) },
                      { key: 'pbRatio', label: 'P/B', value: getFundamentalsNumberDisplay(snapshot.pbRatio, 4) },
                      { key: 'dividendYield', label: 'Dividend Yield', value: snapshot.dividendYield == null ? FUNDAMENTALS_EMPTY_TEXT : `${(snapshot.dividendYield * 100).toFixed(2)}%` },
                      { key: 'totalAssets', label: 'Total Assets', value: formatCompactFinancialValue(snapshot.totalAssets, snapshot.currency) },
                      { key: 'totalLiabilities', label: 'Total Liabilities', value: formatCompactFinancialValue(snapshot.totalLiabilities, snapshot.currency) },
                    ].map((item) => (
                      <Col xs={24} sm={12} lg={8} key={item.key}>
                        <Card size="small">
                          <Text type="secondary">{item.label}</Text>
                          <Title level={5} style={{ margin: '8px 0 0' }}>{item.value}</Title>
                        </Card>
                      </Col>
                    ))}
                  </Row>
                ),
              },
              {
                key: 'annual',
                label: 'Годовые',
                children: (
                  <Table
                    rowKey="id"
                    size="small"
                    pagination={false}
                    scroll={{ x: true }}
                    dataSource={annualPeriods}
                    columns={FINANCIAL_COLUMNS}
                    locale={{ emptyText: 'Нет годовых периодов' }}
                  />
                ),
              },
              {
                key: 'quarterly',
                label: 'Квартальные',
                children: (
                  <Table
                    rowKey="id"
                    size="small"
                    pagination={false}
                    scroll={{ x: true }}
                    dataSource={quarterlyPeriods}
                    columns={FINANCIAL_COLUMNS}
                    locale={{ emptyText: 'Нет квартальных периодов' }}
                  />
                ),
              },
              {
                key: 'earnings',
                label: 'Отчёты',
                children: (
                  <Table
                    rowKey="id"
                    size="small"
                    pagination={false}
                    scroll={{ x: true }}
                    dataSource={data.earningsEvents}
                    columns={EARNINGS_COLUMNS}
                    locale={{ emptyText: 'Нет событий earnings' }}
                  />
                ),
              },
            ]}
          />
        </Space>
      )}
    </Drawer>
  );
};

export default StockFundamentalsDrawer;
