import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react';
import {
  Table,
  Button,
  Modal,
  Form,
  InputNumber,
  Select,
  DatePicker,
  Spin,
  Typography,
  Card,
  Row,
  Col,
  Tag,
  Tabs,
  Popconfirm,
  Tooltip,
  message,
  Input,
} from 'antd';
import { formatCurrency as fmtCur, formatPercent } from '../utils/currency';
import {
  computePortfolioDailyChange,
  getDailyChangeColor,
  getPositionDailyChange,
} from '../utils/portfolioDailyChange';
import {
  PlusOutlined,
  ArrowLeftOutlined,
  EditOutlined,
  DeleteOutlined,
  BellOutlined,
  CaretRightFilled,
  ReloadOutlined,
  SearchOutlined,
  FundOutlined,
} from '@ant-design/icons';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import dayjs, { type Dayjs } from 'dayjs';
import utc from 'dayjs/plugin/utc';
import {
  getPortfolio,
  getStocks,
  addPortfolioItem,
  updatePortfolioItem,
  deletePortfolioItem,
  getPortfolios,
  getOrders,
  createOrder,
  updateOrder,
  deleteOrder,
  getBalance,
  getTransactions,
  createTransaction,
  updateTransaction,
  deleteTransaction,
  getStockPrice,
  updateStockQuote,
} from '../services/api';
import AuthenticatedShell from '../components/AuthenticatedShell';
import StockPriceChart from '../components/StockPriceChart';
import StockFundamentalsDrawer from '../components/StockFundamentalsDrawer';
import StockExchangeTag, { EXCHANGE_ABBREVIATION } from '../components/StockExchangeTag';
import { useAuth } from '../contexts/AuthContext';
import { isQuoteDelayed } from '../utils/quote';
import { applyPersistedQuoteSnapshot, buildQuotePatch } from '../utils/quotePersistence';
import {
  buildRefreshStockSet,
  resolveEffectiveQuote,
  type EffectiveQuote,
} from '../utils/effectiveQuote';
import type {
  Portfolio,
  Stock,
  PortfolioItem,
  Order,
  OrderType,
  OrderStatus,
  Transaction,
  TransactionType,
  InstrumentCodeType,
  PortfolioBalance,
  StockQuoteResponse,
  UpdateStockQuoteRequest,
} from '../types';

/** Typed form values for the create/edit transaction modal. */
interface TxFormValues {
  type: TransactionType;
  amount: number;
  createdAt: Dayjs;
  stockId?: number | null;
  description?: string;
  instrumentCodeType?: InstrumentCodeType | null;
  instrumentCode?: string | null;
  quantity?: number | null;
  unitPrice?: number | null;
}

/**
 * Derives snapshot code and code type from a stock.
 * Prefers trimmed ISIN when non-empty, otherwise trimmed ticker.
 */
export const deriveSnapshotFromStock = (
  stock: Stock,
): { instrumentCode: string; instrumentCodeType: InstrumentCodeType } | null => {
  const isin = stock.isin?.trim();
  if (isin) return { instrumentCode: isin, instrumentCodeType: 'ISIN' };
  const ticker = stock.ticker?.trim();
  if (ticker) return { instrumentCode: ticker, instrumentCodeType: 'Ticker' };
  return null;
};

/** Validates a normalized 12-char ISIN: 2 alpha + 9 alphanumeric + 1 digit. */
export const isValidIsin = (code: string): boolean => /^[A-Z]{2}[A-Z0-9]{9}[0-9]$/.test(code.trim().toUpperCase());

/** Validates a ticker: non-blank, max 32 chars. */
export const isValidTicker = (code: string): boolean => {
  const t = code.trim();
  return t.length > 0 && t.length <= 32;
};

dayjs.extend(utc);

const { Title, Text } = Typography;

/** Accessible label for the quote-refresh button in the positions table toolbar. */
export const REFRESH_QUOTES_LABEL = 'Обновить цены';
export const PORTFOLIO_SILENT_REFRESH_INTERVAL_MS = 5 * 60 * 1000;

/** Ant Design Card size used for the compact summary rows above the positions table. */
export const SUMMARY_CARD_SIZE = 'small' as const;

/** Row gutter used for the compact summary rows above the positions table [horizontal, vertical]. */
export const SUMMARY_ROW_GUTTER: [number, number] = [16, 8];

/** Bottom margin (px) applied to the last summary row before the positions table. */
export const SUMMARY_ROW_MARGIN_BOTTOM = 12;
export const PORTFOLIO_POSITION_RIGHT_ALIGNED_MONEY_KEYS = ['buyPrice', 'currentPrice', 'dailyPriceChange', 'currentValue', 'dailyPositionChange', 'pnlEur'] as const;
export const PORTFOLIO_PENDING_ORDER_RIGHT_ALIGNED_MONEY_KEYS = ['price', 'stopLoss', 'stopMarket', 'currentPrice'] as const;
export const PORTFOLIO_EXECUTED_ORDER_RIGHT_ALIGNED_MONEY_KEYS = ['price', 'total'] as const;
export const PORTFOLIO_TRANSACTION_RIGHT_ALIGNED_MONEY_KEYS = ['amount'] as const;

export { buildQuotePatch } from '../utils/quotePersistence';

type PositionChartRow = { _isPositionChartRow: true; _itemId: number; _stockId: number };
type PositionTableRow = PortfolioItem | PositionChartRow;
const isPositionChartRow = (row: PositionTableRow): row is PositionChartRow =>
  !!(row as PositionChartRow)._isPositionChartRow;
const TOTAL_POS_COLS = 11;

const ORDER_TYPE_LABELS: Record<OrderType, string> = { Buy: 'Покупка', Sell: 'Продажа' };
const ORDER_STATUS_LABELS: Record<OrderStatus, string> = { Pending: 'Ожидание', Executed: 'Выполнено', Cancelled: 'Отменено' };
const ORDER_STATUS_COLORS: Record<OrderStatus, string> = { Pending: 'gold', Executed: 'green', Cancelled: 'red' };
const ORDER_TYPE_COLORS: Record<OrderType, string> = { Buy: 'blue', Sell: 'volcano' };

const TX_TYPE_LABELS: Record<TransactionType, string> = {
  Deposit: 'Пополнение',
  Withdrawal: 'Вывод',
  Buy: 'Покупка',
  Sell: 'Продажа',
  Dividend: 'Дивиденды',
};

const TX_TYPE_COLORS: Record<TransactionType, string> = {
  Deposit: 'green',
  Withdrawal: 'red',
  Buy: 'blue',
  Sell: 'cyan',
  Dividend: 'purple',
};


const getEffectiveSignedAmount = (t: Transaction) => {
  if (t.signedAmount !== 0 || t.amount === 0) return t.signedAmount;
  return t.type === 'Deposit' ? t.amount : -t.amount;
};

/**
 * Computes the net cash remainder from ALL portfolio transactions.
 * Deposits, sales, and dividends increase it; withdrawals and buys decrease it.
 */
export const computeTransactionRemainder = (transactions: Transaction[]): number =>
  transactions.reduce((sum, t) => sum + getEffectiveSignedAmount(t), 0);

/**
 * Computes the total portfolio value as stock value + cash remainder.
 */
export const computeTransactionPortfolioTotal = (stocksValue: number, remainder: number): number =>
  stocksValue + remainder;

/**
 * Buy and sell transactions change the portfolio position table, while cash/dividend transactions do not.
 */
export const shouldRefreshPortfolioAfterTransaction = (type: TransactionType): boolean =>
  type === 'Buy' || type === 'Sell';

/**
 * Computes absolute (positive) totals per transaction type across all supplied transactions.
 */
export const computeTransactionTypeTotals = (
  transactions: Transaction[],
): Record<TransactionType, number> => {
  const result: Record<TransactionType, number> = {
    Deposit: 0,
    Withdrawal: 0,
    Buy: 0,
    Sell: 0,
    Dividend: 0,
  };
  for (const t of transactions) {
    result[t.type] += t.amount;
  }
  return result;
};

export const getTransactionDescription = (t: Transaction): string => {
  const description = t.description?.trim();
  if (description) return description;
  if (t.stock) {
    const exAbbr = EXCHANGE_ABBREVIATION[t.stock.exchange] ?? t.stock.exchange;
    return `${TX_TYPE_LABELS[t.type]} — ${t.stock.ticker} [${exAbbr}] · ${t.stock.name}`;
  }
  return TX_TYPE_LABELS[t.type];
};

const formatCurrency = (value: number) => fmtCur(value, '€');

/**
 * Pure helper that applies all four transaction filters with AND semantics.
 * Exported so it can be unit-tested independently of the React component.
 */
export const filterTransactions = (
  transactions: Transaction[],
  typeFilter: TransactionType | 'all',
  dateFrom: Dayjs | null,
  dateTo: Dayjs | null,
  textQuery: string,
): Transaction[] => {
  const q = textQuery.trim().toLowerCase();
  return transactions.filter((t) => {
    // 1. Type filter
    if (typeFilter !== 'all' && t.type !== typeFilter) return false;
    // 2. Date from (inclusive, day boundary)
    if (dateFrom && dayjs.utc(t.createdAt).local().isBefore(dateFrom.startOf('day'))) return false;
    // 3. Date to (inclusive, end of day)
    if (dateTo && dayjs.utc(t.createdAt).local().isAfter(dateTo.endOf('day'))) return false;
    // 4. Text search
    if (q) {
      const stock = t.stock;
      const generatedDesc = stock
        ? `${TX_TYPE_LABELS[t.type]} — ${stock.ticker} · ${stock.name}`
        : TX_TYPE_LABELS[t.type];
      const haystack = [
        t.description ?? '',
        generatedDesc,
        stock?.ticker ?? '',
        stock?.name ?? '',
        stock?.commonName ?? '',
        stock?.isin ?? '',
        stock?.wkn ?? '',
        TX_TYPE_LABELS[t.type],
      ]
        .join(' ')
        .toLowerCase();
      if (!haystack.includes(q)) return false;
    }
    return true;
  });
};

const PortfolioDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const [searchParams] = useSearchParams();
  // Supported sections: positions | transactions
  const section = searchParams.get('section') ?? 'positions';

  const [portfolio, setPortfolio] = useState<Portfolio | null>(null);
  const [stocks, setStocks] = useState<Stock[]>([]);
  const [portfolios, setPortfolios] = useState<Portfolio[]>([]);
  const [orders, setOrders] = useState<Order[]>([]);
  const [loading, setLoading] = useState(true);

  // Finance state
  const [balance, setBalance] = useState<PortfolioBalance | null>(null);
  const [transactions, setTransactions] = useState<Transaction[]>([]);
  const [txTypeFilter, setTxTypeFilter] = useState<TransactionType | 'all'>('all');
  const [txDateFrom, setTxDateFrom] = useState<Dayjs | null>(null);
  const [txDateTo, setTxDateTo] = useState<Dayjs | null>(null);
  const [txTextFilter, setTxTextFilter] = useState('');
  const [financeLoaded, setFinanceLoaded] = useState(false);

  // Transaction modal
  const [txModalOpen, setTxModalOpen] = useState(false);
  const [editingTx, setEditingTx] = useState<Transaction | null>(null);
  const [txSubmitting, setTxSubmitting] = useState(false);
  const [txForm] = Form.useForm<TxFormValues>();
  const [txType, setTxType] = useState<TransactionType>('Deposit');
  const [txInstrumentCodeType, setTxInstrumentCodeType] = useState<'ISIN' | 'Ticker' | null>(null);
  // Track whether current code/type values were auto-derived (to allow overwrite on stock change)
  const txCodeAutoDerived = useRef(false);

  // Position modal
  const [posModalOpen, setPosModalOpen] = useState(false);
  const [editingItem, setEditingItem] = useState<PortfolioItem | null>(null);
  const [posSubmitting, setPosSubmitting] = useState(false);
  const [posForm] = Form.useForm();

  // Position chart
  const [expandedPositionId, setExpandedPositionId] = useState<number | null>(null);
  const [fundamentalsStock, setFundamentalsStock] = useState<Stock | null>(null);

  // Order modal
  const [orderModalOpen, setOrderModalOpen] = useState(false);
  const [editingOrder, setEditingOrder] = useState<Order | null>(null);
  const [orderSubmitting, setOrderSubmitting] = useState(false);
  const [orderForm] = Form.useForm();

  // Quote refresh
  const [quotesRefreshing, setQuotesRefreshing] = useState(false);
  const quotesRefreshingRef = useRef(false);
  const silentRefetchInFlightRef = useRef<Promise<void> | null>(null);

  const { user, logout } = useAuth();
  const navigate = useNavigate();

  const fetchData = useCallback(async (silent = false) => {
    if (!id) return;
    if (!silent) {
      setLoading(true);
    }
    try {
      const [portfolioRes, stocksRes, portfoliosRes, ordersRes] = await Promise.all([
        getPortfolio(Number(id)),
        getStocks(),
        getPortfolios(),
        getOrders(Number(id)),
      ]);
      setPortfolio(portfolioRes.data);
      setStocks(stocksRes.data);
      setPortfolios(portfoliosRes.data);
      setOrders(ordersRes.data);
    } catch {
      message.error('Ошибка загрузки данных');
    } finally {
      if (!silent) {
        setLoading(false);
      }
    }
  }, [id]);

  const silentRefetchPortfolioData = useCallback(async () => {
    if (silentRefetchInFlightRef.current) {
      await silentRefetchInFlightRef.current;
      return;
    }

    const task = (async () => {
      await fetchData(true);
    })();

    silentRefetchInFlightRef.current = task;
    try {
      await task;
    } finally {
      if (silentRefetchInFlightRef.current === task) {
        silentRefetchInFlightRef.current = null;
      }
    }
  }, [fetchData]);

  useEffect(() => {
    void fetchData();
    setFinanceLoaded(false);
  }, [fetchData, id]);

  useEffect(() => {
    const handleFocus = () => {
      void silentRefetchPortfolioData();
    };

    const handleVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        void silentRefetchPortfolioData();
      }
    };

    const intervalId = window.setInterval(() => {
      if (document.visibilityState === 'visible') {
        void silentRefetchPortfolioData();
      }
    }, PORTFOLIO_SILENT_REFRESH_INTERVAL_MS);

    window.addEventListener('focus', handleFocus);
    document.addEventListener('visibilitychange', handleVisibilityChange);

    return () => {
      window.clearInterval(intervalId);
      window.removeEventListener('focus', handleFocus);
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [silentRefetchPortfolioData]);

  const fetchFinanceData = useCallback(async () => {
    if (!id) return;
    try {
      const [balanceRes, txRes] = await Promise.all([
        getBalance(Number(id)),
        getTransactions(Number(id)),
      ]);
      setBalance(balanceRes.data);
      setTransactions(txRes.data);
      setFinanceLoaded(true);
    } catch {
      message.error('Ошибка загрузки финансовых данных');
    }
  }, [id]);

  useEffect(() => {
    if (section === 'transactions' && !financeLoaded) {
      fetchFinanceData();
    }
  }, [section, id, financeLoaded, fetchFinanceData]);

  useEffect(() => {
    setExpandedPositionId(null);
  }, [id, section]);


  // ── Positions ──────────────────────────────────────────────
  const openAddPosModal = () => { setEditingItem(null); posForm.resetFields(); setPosModalOpen(true); };
  const openEditPosModal = (item: PortfolioItem) => {
    setEditingItem(item);
    posForm.setFieldsValue({ stockId: item.stockId, quantity: item.quantity, buyPrice: item.buyPrice });
    setPosModalOpen(true);
  };
  const handlePosSubmit = async (values: { stockId: number; quantity: number; buyPrice: number }) => {
    if (!id) return;
    setPosSubmitting(true);
    try {
      if (editingItem) {
        await updatePortfolioItem(Number(id), editingItem.id, values);
        message.success('Позиция обновлена');
      } else {
        await addPortfolioItem(Number(id), values);
        message.success('Позиция добавлена');
      }
      setPosModalOpen(false); posForm.resetFields(); fetchData();
    } catch { message.error('Ошибка сохранения позиции'); }
    finally { setPosSubmitting(false); }
  };
  const handleDeleteItem = async (itemId: number) => {
    if (!id) return;
    try { await deletePortfolioItem(Number(id), itemId); message.success('Позиция удалена'); fetchData(); }
    catch { message.error('Ошибка удаления позиции'); }
  };

  // ── Quote refresh ───────────────────────────────────────────
  const handleRefreshPositionPrices = useCallback(async () => {
    if (quotesRefreshingRef.current || !portfolio) return;
    quotesRefreshingRef.current = true;
    setQuotesRefreshing(true);
    try {
      const uniqueStocks = buildRefreshStockSet(
        portfolio.items.map((item) => item.stock),
        stocks,
      );

      const results = await Promise.allSettled(
        uniqueStocks.map(async (stock) => {
          const priceRes = await getStockPrice(stock.ticker, stock.exchange, stock.finanzenNetSlug);
          const quote: StockQuoteResponse = priceRes.data;

          const patch = buildQuotePatch(quote);
          if (!patch) return { stockId: stock.id, patch: null, delayed: isQuoteDelayed(quote) };

          const persisted = (await updateStockQuote(
            stock.id,
            patch satisfies UpdateStockQuoteRequest,
          )).data;

          return { stockId: stock.id, patch: persisted, delayed: isQuoteDelayed(quote) };
        })
      );

      const failed = results.filter((r) => r.status === 'rejected').length;
      const delayed = results.filter(
        (r) => r.status === 'fulfilled' && r.value.delayed,
      ).length;
      const skipped = results.filter(
        (r) => r.status === 'fulfilled' && !r.value.delayed && r.value.patch === null,
      ).length;

      // Patch local state with refreshed quote fields
      const patchMap = new Map<number, NonNullable<Awaited<ReturnType<typeof updateStockQuote>>['data']>>();
      for (const result of results) {
        if (result.status === 'fulfilled' && result.value.patch) {
          patchMap.set(result.value.stockId, result.value.patch);
        }
      }

      const applyPatch = (stock: Stock): Stock => {
        const patch = patchMap.get(stock.id);
        if (!patch) return stock;
        return applyPersistedQuoteSnapshot(stock, patch);
      };

      setStocks((prev) => prev.map(applyPatch));
      setPortfolio((prev) => {
        if (!prev) return prev;
        return {
          ...prev,
          items: prev.items.map((item) => ({
            ...item,
            stock: applyPatch(item.stock),
          })),
        };
      });
      setPortfolios((prev) =>
        prev.map((p) => ({
          ...p,
          items: p.items.map((item) => ({
            ...item,
            stock: applyPatch(item.stock),
          })),
        }))
      );

      if (failed === 0 && delayed === 0 && skipped === 0) {
        message.success('Цены обновлены');
      } else if (delayed > 0 && failed === 0 && skipped === 0) {
        message.warning(`Задержано: ${delayed}. Остальные цены обновлены`);
      } else if (delayed > 0 && (failed > 0 || skipped > 0)) {
        message.warning(`Цены обновлены частично (${failed} ошибок, ${delayed} задержано)`);
      } else if (failed > 0 && delayed === 0) {
        message.warning(`Цены обновлены частично (${failed} ошибок)`);
      } else {
        message.warning(`Цены обновлены частично (${skipped} без конвертации)`);
      }
    } catch {
      message.error('Ошибка обновления цен');
    } finally {
      quotesRefreshingRef.current = false;
      setQuotesRefreshing(false);
    }
  }, [portfolio, stocks]);

  // ── Orders ─────────────────────────────────────────────────
  const openAddOrderModal = () => { setEditingOrder(null); orderForm.resetFields(); setOrderModalOpen(true); };
  const openEditOrderModal = (order: Order) => {
    setEditingOrder(order);
    orderForm.setFieldsValue({
      stockId: order.stockId,
      type: order.type,
      status: order.status,
      quantity: order.quantity,
      price: order.price,
      stopLoss: order.stopLoss ?? undefined,
      stopMarket: order.stopMarket ?? undefined,
    });
    setOrderModalOpen(true);
  };
  const handleOrderSubmit = async (values: {
    stockId: number; type: OrderType; status: OrderStatus;
    quantity: number; price: number; stopLoss?: number; stopMarket?: number;
  }) => {
    if (!id) return;
    setOrderSubmitting(true);
    try {
      if (editingOrder) {
        await updateOrder(Number(id), editingOrder.id, {
          type: values.type, status: values.status,
          quantity: values.quantity, price: values.price,
          stopLoss: values.stopLoss, stopMarket: values.stopMarket,
        });
        message.success('Ордер обновлён');
      } else {
        await createOrder(Number(id), {
          stockId: values.stockId, type: values.type,
          quantity: values.quantity, price: values.price,
          stopLoss: values.stopLoss, stopMarket: values.stopMarket,
        });
        message.success('Ордер создан');
      }
      setOrderModalOpen(false); orderForm.resetFields(); fetchData();
      // Also refresh finance if we're on transactions section (executed order creates tx)
      if (financeLoaded) fetchFinanceData();
    } catch { message.error('Ошибка сохранения ордера'); }
    finally { setOrderSubmitting(false); }
  };
  const handleDeleteOrder = async (orderId: number) => {
    if (!id) return;
    try { await deleteOrder(Number(id), orderId); message.success('Ордер удалён'); fetchData(); }
    catch { message.error('Ошибка удаления ордера'); }
  };

  const isTriggered = (order: Order): string | null => {
    const currentPrice = order.stock?.currentPrice;
    if (!currentPrice || order.status !== 'Pending') return null;
    if (order.stopLoss != null) {
      if (order.type === 'Buy' && currentPrice <= order.stopLoss) return `Цена ${currentPrice} достигла Stop Loss ${order.stopLoss}`;
      if (order.type === 'Sell' && currentPrice <= order.stopLoss) return `Цена ${currentPrice} достигла Stop Loss ${order.stopLoss}`;
    }
    if (order.stopMarket != null) {
      if (order.type === 'Buy' && currentPrice >= order.stopMarket) return `Цена ${currentPrice} достигла Stop Market ${order.stopMarket}`;
      if (order.type === 'Sell' && currentPrice >= order.stopMarket) return `Цена ${currentPrice} достигла Stop Market ${order.stopMarket}`;
    }
    if (order.type === 'Buy' && currentPrice <= order.price) return `Цена ${currentPrice} <= лимит покупки ${order.price}`;
    if (order.type === 'Sell' && currentPrice >= order.price) return `Цена ${currentPrice} >= лимит продажи ${order.price}`;
    return null;
  };

  // ── Transactions ────────────────────────────────────────────
  const openNewTxModal = () => {
    setEditingTx(null);
    txForm.resetFields();
    txCodeAutoDerived.current = false;
   txForm.setFieldsValue({ type: 'Deposit', createdAt: dayjs() });
    setTxType('Deposit');
    setTxInstrumentCodeType(null);
    setTxModalOpen(true);
  };
  const openEditTxModal = (tx: Transaction) => {
    setEditingTx(tx);
    txForm.setFieldsValue({
      type: tx.type,
      amount: tx.amount,
     createdAt: dayjs.utc(tx.createdAt).local(),
     stockId: tx.stockId ?? undefined,
     description: tx.description ?? undefined,
     instrumentCodeType: tx.instrumentCodeType ?? undefined,
     instrumentCode: tx.instrumentCode ?? undefined,
     quantity: tx.quantity ?? undefined,
     unitPrice: tx.unitPrice ?? undefined,
   });
   txCodeAutoDerived.current = false;
   setTxType(tx.type);
   setTxInstrumentCodeType(tx.instrumentCodeType ?? null);
   setTxModalOpen(true);
  };
  const handleTxSubmit = async (values: TxFormValues) => {
   if (!id) return;
   setTxSubmitting(true);
   const hideSnapshot = values.type === 'Deposit' || values.type === 'Withdrawal';
   const shouldRefreshPortfolio = shouldRefreshPortfolioAfterTransaction(values.type);
   const normalizeCode = (v?: string | null): string | null => {
     if (hideSnapshot) return null;
     const t = (v ?? '').trim();
     return t.length > 0 ? t : null;
   };
   try {
     const payload = {
       type: values.type,
       amount: Math.abs(values.amount),
       createdAt: values.createdAt.toISOString(),
       stockId: values.stockId ?? null,
       description: values.description,
       instrumentCode: normalizeCode(values.instrumentCode),
       instrumentCodeType: hideSnapshot ? null : (values.instrumentCodeType ?? null),
       quantity: hideSnapshot ? null : (values.quantity ?? null),
       unitPrice: hideSnapshot ? null : (values.unitPrice ?? null),
     };
     if (editingTx) {
       await updateTransaction(Number(id), editingTx.id, payload);
       message.success('Транзакция обновлена');
      } else {
        await createTransaction(Number(id), payload);
        message.success('Транзакция добавлена');
      }
      setTxModalOpen(false); txForm.resetFields();
      await fetchFinanceData();
      if (shouldRefreshPortfolio) {
        await fetchData();
      }
    } catch { message.error('Ошибка сохранения транзакции'); }
    finally { setTxSubmitting(false); }
  };
  const handleDeleteTx = async (txId: number) => {
    if (!id) return;
    try {
      const tx = transactions.find((item) => item.id === txId);
      await deleteTransaction(Number(id), txId);
      message.success('Транзакция удалена');
      await fetchFinanceData();
      if (tx && shouldRefreshPortfolioAfterTransaction(tx.type)) {
        await fetchData();
      }
    } catch { message.error('Ошибка удаления транзакции'); }
  };

  // ── Summary ────────────────────────────────────────────────
  const computeSummary = (items: PortfolioItem[]) => {
    const totalValue = items.reduce((sum, item) => sum + item.stock.currentPrice * item.quantity, 0);
    const totalCost = items.reduce((sum, item) => sum + item.buyPrice * item.quantity, 0);
    const totalPnlEur = totalValue - totalCost;
    const totalPnlPct = totalCost > 0 ? (totalPnlEur / totalCost) * 100 : 0;
    return { totalValue, totalPnlEur, totalPnlPct, count: items.length };
  };

  const items = portfolio?.items ?? [];

  // When a position's stored price is outside the current 10-minute freshness
  // window (or missing), the most recent fresh quote for an equivalent stock on
  // another exchange is used instead. Identity fields are never replaced.
  const effectiveQuoteMap = useMemo(() => {
    const map = new Map<number, EffectiveQuote>();
    for (const item of items) {
      if (!map.has(item.stock.id)) {
        const eq = resolveEffectiveQuote(item.stock, stocks);
        map.set(item.stock.id, eq);
        if (import.meta.env.DEV) {
          console.info('[effectiveQuote]', eq.diagnosticInfo);
        }
      }
    }
    return map;
  }, [items, stocks]);

  const effectiveItems = useMemo(
    () => items.map((item) => {
      const eq = effectiveQuoteMap.get(item.stock.id);
      if (!eq) return item;
      return {
        ...item,
        stock: {
          ...item.stock,
          currentPrice: eq.currentPrice,
          currentPriceChange: eq.currentPriceChange,
          currentPriceChangePercent: eq.currentPriceChangePercent,
          currentPriceAt: eq.currentPriceAt,
        },
      };
    }),
    [items, effectiveQuoteMap],
  );

  const sortedItems = useMemo(() => {
    return [...effectiveItems].sort((a, b) => {
      const nameA = a.stock?.name ?? '';
      const nameB = b.stock?.name ?? '';
      if (!nameA && nameB) return 1;
      if (nameA && !nameB) return -1;
      const cmp = nameA.localeCompare(nameB, 'ru', { sensitivity: 'base' });
      if (cmp !== 0) return cmp;
      const tickerCmp = (a.stock?.ticker ?? '').localeCompare(b.stock?.ticker ?? '', 'ru', { sensitivity: 'base' });
      if (tickerCmp !== 0) return tickerCmp;
      return a.id - b.id;
    });
  }, [effectiveItems]);

  const summary = computeSummary(effectiveItems);
  const dailyChangeSummary = computePortfolioDailyChange(effectiveItems);

  const pendingOrders = orders.filter((o) => o.status === 'Pending');
  const executedOrders = orders.filter((o) => o.status === 'Executed' || o.status === 'Cancelled');

  const filteredTransactions = useMemo(
    () => filterTransactions(transactions, txTypeFilter, txDateFrom, txDateTo, txTextFilter),
    [transactions, txTypeFilter, txDateFrom, txDateTo, txTextFilter],
  );
  const stockOptions = useMemo(
    () => stocks.map((s) => ({
      value: s.id,
      label: `${s.ticker} [${EXCHANGE_ABBREVIATION[s.exchange] ?? s.exchange}] — ${s.name}`,
    })),
    [stocks],
  );
  const txRemainder = useMemo(() => computeTransactionRemainder(transactions), [transactions]);
  const txTypeTotals = useMemo(() => computeTransactionTypeTotals(transactions), [transactions]);
  const txTotalPortfolioValue = useMemo(
    () => computeTransactionPortfolioTotal(summary.totalValue, txRemainder),
    [summary.totalValue, txRemainder],
  );


  // ── Columns ────────────────────────────────────────────────
  const positionColumns = [
    {
      title: 'Тикер',
      key: 'ticker',
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) {
          const item = items.find((i) => i.id === record._itemId);
          return {
            children: (
              <StockPriceChart
                panelId={`pos-chart-panel-${record._itemId}`}
                stockId={record._stockId}
                ticker={item?.stock?.ticker ?? ''}
                name={item?.stock?.name ?? ''}
                exchange={item?.stock?.exchange ?? null}
                providerSymbol={item?.stock?.providerSymbol ?? null}
                wkn={item?.stock?.wkn ?? null}
                isin={item?.stock?.isin ?? null}
                finanzenNetSlug={item?.stock?.finanzenNetSlug ?? null}
                storedPriceEur={item?.stock?.currentPrice ?? null}
                storedPriceChangeEur={item?.stock?.currentPriceChange ?? null}
                storedPriceTimestampUtc={item?.stock?.currentPriceAt ?? null}
              />
            ),
            props: { colSpan: TOTAL_POS_COLS },
          };
        }
        const item = record as PortfolioItem;
        const ticker = item.stock?.ticker;
        if (!ticker) return <Tag color="blue">—</Tag>;
        const isExpanded = expandedPositionId === item.id;
        return (
          <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <button
              type="button"
              onClick={() => setExpandedPositionId((prev) => (prev === item.id ? null : item.id))}
              aria-expanded={isExpanded}
              aria-controls={`pos-chart-panel-${item.id}`}
              aria-label={isExpanded ? `Закрыть график цены: ${ticker}` : `Открыть график цены: ${ticker}`}
              style={{
                padding: 0, background: 'none', border: 'none', cursor: 'pointer',
                fontWeight: 600, color: isExpanded ? '#1677ff' : 'inherit',
                display: 'inline-flex', alignItems: 'center', gap: 4,
              }}
            >
              <CaretRightFilled
                style={{
                  fontSize: 10, transition: 'transform 0.2s',
                  transform: isExpanded ? 'rotate(90deg)' : 'rotate(0deg)',
                  color: '#1677ff',
                }}
              />
              {ticker}
            </button>
            {item.stock?.exchange && <StockExchangeTag exchange={item.stock.exchange} />}
          </div>
        );
      },
    },
    {
      title: 'Название', key: 'name',
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) return { children: null, props: { colSpan: 0 } };
        return (record as PortfolioItem).stock?.name ?? '—';
      },
    },
    {
      title: 'Кол-во', key: 'quantity',
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) return { children: null, props: { colSpan: 0 } };
        return (record as PortfolioItem).quantity.toFixed(2);
      },
    },
    {
      title: 'Цена покупки', key: 'buyPrice', align: 'right' as const,
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) return { children: null, props: { colSpan: 0 } };
        return fmtCur((record as PortfolioItem).buyPrice, '€');
      },
    },
    {
      title: 'Тек. цена', key: 'currentPrice', align: 'right' as const,
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const item = record as PortfolioItem;
        const priceNode = fmtCur(item.stock.currentPrice, '€');
        const eq = effectiveQuoteMap.get(item.stock.id);
        const diagStyle: React.CSSProperties = { whiteSpace: 'pre-wrap', fontFamily: 'monospace', fontSize: 16 };
        if (eq?.sourceExchange) {
          const abbr = EXCHANGE_ABBREVIATION[eq.sourceExchange] ?? eq.sourceExchange;
          return (
            <Tooltip title={<span style={diagStyle}>{`Котировка с биржи ${abbr}\n\n${eq.diagnosticInfo}`}</span>}>
              <span style={{ whiteSpace: 'nowrap', cursor: 'default' }}>
                {priceNode}{' '}
                <Tag style={{ fontSize: 16, lineHeight: '22px', padding: '0 3px', marginInlineEnd: 0, opacity: 0.85 }}>{abbr}</Tag>
              </span>
            </Tooltip>
          );
        }
        if (eq?.diagnosticInfo) {
          return (
            <Tooltip title={<span style={diagStyle}>{eq.diagnosticInfo}</span>}>
              <span style={{ cursor: 'default' }}>{priceNode}</span>
            </Tooltip>
          );
        }
        return priceNode;
      },
    },
    {
      title: 'Изм. за день',
      key: 'dailyPriceChange',
      align: 'right' as const,
      width: 90,
      className: 'col-compact',
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) {
          return { children: null, props: { colSpan: 0 } };
        }
        const change = (record as PortfolioItem).stock.currentPriceChange ?? null;
        return (
          <span style={{ color: getDailyChangeColor(change), whiteSpace: 'nowrap' }}>
            {fmtCur(change, '€', { signed: true })}
          </span>
        );
      },
    },
    {
      title: <><span>Тек.</span><br /><span>стоимость</span></>, key: 'currentValue', align: 'right' as const,
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const r = record as PortfolioItem;
        return fmtCur(r.stock.currentPrice * r.quantity, '€');
      },
    },
    {
      title: 'Изм. за день',
      key: 'dailyPositionChange',
      align: 'right' as const,
      width: 90,
      className: 'col-compact',
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) {
          return { children: null, props: { colSpan: 0 } };
        }
        const change = getPositionDailyChange(record as PortfolioItem);
        return (
          <span style={{ color: getDailyChangeColor(change), whiteSpace: 'nowrap' }}>
            {fmtCur(change, '€', { signed: true })}
          </span>
        );
      },
    },
    {
      title: 'P&L (€)', key: 'pnlEur', align: 'right' as const,
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const r = record as PortfolioItem;
        const pnl = (r.stock.currentPrice - r.buyPrice) * r.quantity;
        return <span style={{ color: pnl >= 0 ? '#3f8600' : '#cf1322' }}>{fmtCur(pnl, '€', { signed: true })}</span>;
      },
    },
    {
      title: 'P&L (%)', key: 'pnlPct',
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const r = record as PortfolioItem;
        const pct = r.buyPrice > 0 ? ((r.stock.currentPrice - r.buyPrice) / r.buyPrice) * 100 : 0;
        return <span style={{ color: pct >= 0 ? '#3f8600' : '#cf1322' }}>{pct >= 0 ? '+' : ''}{pct.toFixed(2)}%</span>;
      },
    },
    {
      title: 'Действия', key: 'actions',
      render: (_: unknown, record: PositionTableRow) => {
        if (isPositionChartRow(record)) return { children: null, props: { colSpan: 0 } };
        const r = record as PortfolioItem;
        return (
          <div style={{ display: 'flex', gap: 6 }}>
            <Tooltip title="Фундаментальные данные">
              <Button
                icon={<FundOutlined />}
                size="small"
                aria-label="Фундаментальные данные"
                onClick={() => setFundamentalsStock(r.stock)}
              />
            </Tooltip>
            <Tooltip title="Изменить">
              <Button icon={<EditOutlined />} size="small" aria-label="Изменить" onClick={() => openEditPosModal(r)} />
            </Tooltip>
            <Popconfirm title="Удалить позицию?" onConfirm={() => handleDeleteItem(r.id)} okText="Да" cancelText="Нет">
              <Tooltip title="Удалить">
                <Button icon={<DeleteOutlined />} size="small" aria-label="Удалить" />
              </Tooltip>
            </Popconfirm>
          </div>
        );
      },
    },
  ];

  const pendingOrderColumns = [
    {
      title: '', key: 'alert', width: 32,
      render: (_: unknown, r: Order) => {
        const msg = isTriggered(r);
        return msg ? <Tooltip title={msg}><BellOutlined style={{ color: '#faad14', fontSize: 16 }} /></Tooltip> : null;
      },
    },
    { title: 'Тикер', key: 'ticker', render: (_: unknown, r: Order) => (
      <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
        <Tag color="blue">{r.stock?.ticker ?? '—'}</Tag>
        {r.stock?.exchange && <StockExchangeTag exchange={r.stock.exchange} />}
      </div>
    ) },
    { title: 'Название', key: 'name', render: (_: unknown, r: Order) => r.stock?.name ?? '—' },
    { title: 'Тип', dataIndex: 'type', key: 'type', render: (v: OrderType) => <Tag color={ORDER_TYPE_COLORS[v]}>{ORDER_TYPE_LABELS[v]}</Tag> },
    { title: 'Кол-во', dataIndex: 'quantity', key: 'quantity', render: (v: number) => v.toFixed(2) },
    { title: 'Цена', dataIndex: 'price', key: 'price', align: 'right' as const, render: (v: number) => fmtCur(v, '€') },
    { title: 'Stop Loss', dataIndex: 'stopLoss', key: 'stopLoss', align: 'right' as const, render: (v: number | null) => fmtCur(v, '€') },
    { title: 'Stop Market', dataIndex: 'stopMarket', key: 'stopMarket', align: 'right' as const, render: (v: number | null) => fmtCur(v, '€') },
    { title: 'Тек. цена', key: 'currentPrice', align: 'right' as const, render: (_: unknown, r: Order) => fmtCur(r.stock?.currentPrice ?? null, '€') },
    { title: 'Создан', dataIndex: 'createdAt', key: 'createdAt', render: (v: string) => dayjs.utc(v).local().format('DD.MM.YYYY HH:mm') },
    {
      title: 'Действия', key: 'actions',
      render: (_: unknown, r: Order) => (
        <div style={{ display: 'flex', gap: 8 }}>
          <Button icon={<EditOutlined />} size="small" onClick={() => openEditOrderModal(r)}>Изменить</Button>
          <Popconfirm title="Удалить ордер?" onConfirm={() => handleDeleteOrder(r.id)} okText="Да" cancelText="Нет">
            <Button icon={<DeleteOutlined />} size="small">Удалить</Button>
          </Popconfirm>
        </div>
      ),
    },
  ];

  const executedOrderColumns = [
    { title: 'Дата исполнения', dataIndex: 'executedAt', key: 'executedAt', render: (v: string | null) => v ? dayjs.utc(v).local().format('DD.MM.YYYY') : '—' },
    { title: 'Тикер', key: 'ticker', render: (_: unknown, r: Order) => (
      <div style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
        <Tag color="blue">{r.stock?.ticker ?? '—'}</Tag>
        {r.stock?.exchange && <StockExchangeTag exchange={r.stock.exchange} />}
      </div>
    ) },
    { title: 'Название', key: 'name', render: (_: unknown, r: Order) => r.stock?.name ?? '—' },
    { title: 'Тип', dataIndex: 'type', key: 'type', render: (v: OrderType) => <Tag color={ORDER_TYPE_COLORS[v]}>{ORDER_TYPE_LABELS[v]}</Tag> },
    { title: 'Статус', dataIndex: 'status', key: 'status', render: (v: OrderStatus) => <Tag color={ORDER_STATUS_COLORS[v]}>{ORDER_STATUS_LABELS[v]}</Tag> },
    { title: 'Кол-во', dataIndex: 'quantity', key: 'quantity', render: (v: number) => v.toFixed(2) },
    { title: 'Цена', dataIndex: 'price', key: 'price', align: 'right' as const, render: (v: number) => fmtCur(v, '€') },
    { title: 'Итого', key: 'total', align: 'right' as const, render: (_: unknown, r: Order) => fmtCur(r.price * r.quantity, '€') },
    {
      title: 'Удалить', key: 'delete',
      render: (_: unknown, r: Order) => (
        <Popconfirm title="Удалить ордер?" onConfirm={() => handleDeleteOrder(r.id)} okText="Да" cancelText="Нет">
          <Button icon={<DeleteOutlined />} size="small">Удалить</Button>
        </Popconfirm>
      ),
    },
  ];

  // ── Derived keys ───────────────────────────────────────────
  const sectionKey = `portfolio-${id}-${section}`;
  const sidebarOpenKeys = ['portfolios', `portfolio-${id}`];

  // Position table data with inline chart rows
  const positionTableData: PositionTableRow[] = useMemo(() => {
    const rows: PositionTableRow[] = [];
    for (const item of sortedItems) {
      rows.push(item);
      if (expandedPositionId === item.id) {
        rows.push({ _isPositionChartRow: true, _itemId: item.id, _stockId: item.stockId });
      }
    }
    return rows;
  }, [sortedItems, expandedPositionId]);

  return (
    <>
      <AuthenticatedShell
        portfolios={portfolios}
        selectedKeys={[sectionKey]}
        userName={user?.username}
        onLogout={logout}
        defaultOpenKeys={sidebarOpenKeys}
        activePortfolioId={id}
        headerLeft={(
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/')}>Назад</Button>
            <Title level={4} style={{ margin: 0 }}>{portfolio?.name ?? 'Портфель'}</Title>
          </div>
        )}
      >
          {loading ? (
            <div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}><Spin size="large" /></div>
          ) : (
            <>
              {section === 'positions' && (
                <>
                  <Row gutter={SUMMARY_ROW_GUTTER} style={{ marginBottom: 0 }}>
                    <Col xs={24} sm={12} lg={6}>
                      <Card size={SUMMARY_CARD_SIZE}><Text type="secondary">Общая стоимость</Text><Title level={4} style={{ margin: 0 }}>{formatCurrency(summary.totalValue)}</Title></Card>
                    </Col>
                    <Col xs={24} sm={12} lg={6}>
                      <Card size={SUMMARY_CARD_SIZE}>
                        <Text type="secondary">Общий P&L (€)</Text>
                        <Title level={4} style={{ margin: 0, color: summary.totalPnlEur >= 0 ? '#3f8600' : '#cf1322' }}>
                          {summary.totalPnlEur >= 0 ? '+' : ''}{formatCurrency(summary.totalPnlEur)}
                        </Title>
                      </Card>
                    </Col>
                    <Col xs={24} sm={12} lg={6}>
                      <Card size={SUMMARY_CARD_SIZE}>
                        <Text type="secondary">Общий P&L (%)</Text>
                        <Title level={4} style={{ margin: 0, color: summary.totalPnlPct >= 0 ? '#3f8600' : '#cf1322' }}>
                          {summary.totalPnlPct >= 0 ? '+' : ''}{summary.totalPnlPct.toFixed(2)}%
                        </Title>
                      </Card>
                    </Col>
                    <Col xs={24} sm={12} lg={6}>
                      <Card size={SUMMARY_CARD_SIZE}><Text type="secondary">Позиций</Text><Title level={4} style={{ margin: 0 }}>{summary.count}</Title></Card>
                    </Col>
                  </Row>
                  <Row gutter={SUMMARY_ROW_GUTTER} style={{ marginBottom: SUMMARY_ROW_MARGIN_BOTTOM }}>
                    <Col xs={0} lg={6} />
                    <Col xs={24} sm={12} lg={6}>
                      <Card size={SUMMARY_CARD_SIZE}>
                        <Text type="secondary">Изменение за день (€)</Text>
                        <Title
                          level={4}
                          style={{
                            margin: 0,
                            color: getDailyChangeColor(dailyChangeSummary.changeEur),
                          }}
                        >
                          {fmtCur(dailyChangeSummary.changeEur, '€', { signed: true })}
                        </Title>
                      </Card>
                    </Col>
                    <Col xs={24} sm={12} lg={6}>
                      <Card size={SUMMARY_CARD_SIZE}>
                        <Text type="secondary">Изменение за день (%)</Text>
                        <Title
                          level={4}
                          style={{
                            margin: 0,
                            color: getDailyChangeColor(dailyChangeSummary.changePercent),
                          }}
                        >
                          {formatPercent(dailyChangeSummary.changePercent)}
                        </Title>
                      </Card>
                    </Col>
                    <Col xs={0} lg={6} />
                  </Row>
                </>
              )}

              {/* ── Positions (with Orders as tab) ─────────────── */}
              {section === 'positions' && (
                <Tabs
                  defaultActiveKey="positions"
                  tabBarExtraContent={
                    <div style={{ display: 'flex', gap: 8 }}>
                      <Tooltip title="Обновить цены">
                        <Button
                          icon={<ReloadOutlined />}
                          loading={quotesRefreshing}
                          disabled={quotesRefreshing}
                          aria-label="Обновить цены"
                          onClick={handleRefreshPositionPrices}
                        />
                      </Tooltip>
                      <Button type="primary" icon={<PlusOutlined />} onClick={openAddPosModal}>
                        Добавить позицию
                      </Button>
                      <Button icon={<PlusOutlined />} onClick={openAddOrderModal}>
                        Создать ордер
                      </Button>
                    </div>
                  }
                  items={[
                    {
                      key: 'positions',
                      label: 'Позиции',
                      children: (
                        <Table
                          className="positions-table"
                          dataSource={positionTableData}
                          columns={positionColumns}
                          rowKey={(record: PositionTableRow) =>
                            isPositionChartRow(record)
                              ? `pos-chart-${record._itemId}`
                              : String((record as PortfolioItem).id)
                          }
                          rowClassName={(record: PositionTableRow) =>
                            isPositionChartRow(record) ? 'chart-panel-row' : ''
                          }
                          scroll={{ x: true }}
                          pagination={false}
                        />
                      ),
                    },
                    {
                      key: 'orders',
                      label: (
                        <span>
                          Ордера
                          {pendingOrders.length > 0 && (
                            <Tag color="gold" style={{ marginLeft: 6 }}>{pendingOrders.length}</Tag>
                          )}
                        </span>
                      ),
                      children: (
                        <Tabs
                          defaultActiveKey="pending"
                          items={[
                            {
                              key: 'pending',
                              label: (
                                <span>
                                  Ожидающие
                                  {pendingOrders.length > 0 && (
                                    <Tag color="gold" style={{ marginLeft: 6 }}>{pendingOrders.length}</Tag>
                                  )}
                                </span>
                              ),
                              children: (
                                <Table
                                  dataSource={pendingOrders}
                                  columns={pendingOrderColumns}
                                  rowKey="id"
                                  scroll={{ x: true }}
                                  pagination={{ pageSize: 20 }}
                                  locale={{ emptyText: 'Нет ожидающих ордеров' }}
                                />
                              ),
                            },
                            {
                              key: 'executed',
                              label: (
                                <span>
                                  Выполненные
                                  {executedOrders.length > 0 && (
                                    <Tag color="green" style={{ marginLeft: 6 }}>{executedOrders.length}</Tag>
                                  )}
                                </span>
                              ),
                              children: (
                                <Table
                                  dataSource={executedOrders}
                                  columns={executedOrderColumns}
                                  rowKey="id"
                                  scroll={{ x: true }}
                                  pagination={{ pageSize: 20 }}
                                  locale={{ emptyText: 'Нет выполненных ордеров' }}
                                />
                              ),
                            },
                          ]}
                        />
                      ),
                    },
                  ]}
                />
              )}

              {/* ── Transactions ──────────────────────────────────── */}
              {section === 'transactions' && (
                financeLoaded ? (
                  <>
                    {/* Balance summary */}
                    {balance && (
                      <div style={{ display: 'flex', gap: 16, marginBottom: 16, flexWrap: 'wrap' }}>
                        <div style={{ flex: '1 1 0', minWidth: 120 }}>
                          <Card style={{ height: '100%' }}>
                            <Text type="secondary">Итого портфель</Text>
                            <Title level={4} style={{ margin: 0 }}>
                              {formatCurrency(txTotalPortfolioValue)}
                            </Title>
                          </Card>
                        </div>
                        <div style={{ flex: '1 1 0', minWidth: 120 }}>
                          <Card style={{ height: '100%' }}>
                            <Text type="secondary">Стоимость акций</Text>
                            <Title level={4} style={{ margin: 0 }}>{formatCurrency(summary.totalValue)}</Title>
                          </Card>
                        </div>
                        <div style={{ flex: '1 1 0', minWidth: 120 }}>
                          <Card style={{ height: '100%' }}>
                            <Text type="secondary">Остаток</Text>
                            <Title level={4} style={{ margin: 0 }}>{formatCurrency(txRemainder)}</Title>
                          </Card>
                        </div>
                      </div>
                    )}

                    {/* Transaction type totals */}
                    <div style={{ display: 'flex', gap: 16, marginBottom: 16, flexWrap: 'wrap' }}>
                      <div style={{ flex: '1 1 0', minWidth: 100 }}>
                        <Card size={SUMMARY_CARD_SIZE} style={{ height: '100%' }}>
                          <Text type="secondary">Пополнения</Text>
                          <div><Text strong>{formatCurrency(txTypeTotals.Deposit)}</Text></div>
                        </Card>
                      </div>
                      <div style={{ flex: '1 1 0', minWidth: 100 }}>
                        <Card size={SUMMARY_CARD_SIZE} style={{ height: '100%' }}>
                          <Text type="secondary">Вывод</Text>
                          <div><Text strong>{formatCurrency(txTypeTotals.Withdrawal)}</Text></div>
                        </Card>
                      </div>
                      <div style={{ flex: '1 1 0', minWidth: 100 }}>
                        <Card size={SUMMARY_CARD_SIZE} style={{ height: '100%' }}>
                          <Text type="secondary">Покупка</Text>
                          <div><Text strong>{formatCurrency(txTypeTotals.Buy)}</Text></div>
                        </Card>
                      </div>
                      <div style={{ flex: '1 1 0', minWidth: 100 }}>
                        <Card size={SUMMARY_CARD_SIZE} style={{ height: '100%' }}>
                          <Text type="secondary">Продажа</Text>
                          <div><Text strong>{formatCurrency(txTypeTotals.Sell)}</Text></div>
                        </Card>
                      </div>
                      <div style={{ flex: '1 1 0', minWidth: 100 }}>
                        <Card size={SUMMARY_CARD_SIZE} style={{ height: '100%' }}>
                          <Text type="secondary">Дивиденды</Text>
                          <div><Text strong>{formatCurrency(txTypeTotals.Dividend)}</Text></div>
                        </Card>
                      </div>
                    </div>

                    {/* Toolbar */}
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16, flexWrap: 'wrap', gap: 8 }}>
                      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center' }}>
                        <Select
                          value={txTypeFilter}
                          onChange={(v) => setTxTypeFilter(v)}
                          style={{ minWidth: 160 }}
                          options={[
                            { value: 'all', label: 'Все типы' },
                            { value: 'Deposit', label: 'Пополнение' },
                            { value: 'Withdrawal', label: 'Вывод' },
                            { value: 'Buy', label: 'Покупка' },
                            { value: 'Sell', label: 'Продажа' },
                            { value: 'Dividend', label: 'Дивиденды' },
                          ]}
                        />
                        <DatePicker
                          placeholder="Дата от"
                          value={txDateFrom}
                          onChange={(d) => setTxDateFrom(d)}
                          style={{ width: 140 }}
                          format="DD.MM.YYYY"
                          allowClear
                        />
                        <DatePicker
                          placeholder="Дата до"
                          value={txDateTo}
                          onChange={(d) => setTxDateTo(d)}
                          style={{ width: 140 }}
                          format="DD.MM.YYYY"
                          allowClear
                        />
                        <Input
                          placeholder="Поиск по описанию, тикеру…"
                          prefix={<SearchOutlined />}
                          allowClear
                          value={txTextFilter}
                          onChange={(e) => setTxTextFilter(e.target.value)}
                          style={{ width: 220 }}
                        />
                      </div>
                      <Button type="primary" icon={<PlusOutlined />} onClick={openNewTxModal}>
                        Новая транзакция
                      </Button>
                    </div>

                    {/* Transaction journal */}
                    <Table
                      className="transactions-table"
                      dataSource={filteredTransactions}
                      rowKey="id"
                      scroll={{ x: true }}
                      pagination={{ pageSize: 20 }}
                      columns={[
                        {
                          title: 'Дата', dataIndex: 'createdAt', key: 'createdAt',
                          render: (v: string) => dayjs.utc(v).local().format('DD.MM.YYYY HH:mm'),
                          sorter: (a: Transaction, b: Transaction) =>
                            dayjs(a.createdAt).valueOf() - dayjs(b.createdAt).valueOf(),
                          defaultSortOrder: 'descend',
                        },
                        {
                          title: 'Тип', dataIndex: 'type', key: 'type',
                          render: (v: TransactionType) => (
                            <Tag color={TX_TYPE_COLORS[v]}>{TX_TYPE_LABELS[v]}</Tag>
                          ),
                        },
                        {
                          title: 'Сумма', key: 'amount', align: 'right' as const,
                          render: (_: unknown, t: Transaction) => {
                            const signed = getEffectiveSignedAmount(t);
                            const color = signed >= 0 ? '#3f8600' : '#cf1322';
                            return <span style={{ color }}>{fmtCur(signed, '€', { signed: true })}</span>;
                          },
                        },
                        {
                          title: 'Описание', key: 'description',
                          render: (_: unknown, t: Transaction) => getTransactionDescription(t),
                        },
                        {
                          title: 'Действия', key: 'actions',
                          render: (_: unknown, t: Transaction) => (
                            <div style={{ display: 'flex', gap: 6 }}>
                              {!t.orderId && (
                                <Tooltip title="Редактировать">
                                  <Button
                                    icon={<EditOutlined />}
                                    size="small"
                                    aria-label="Редактировать транзакцию"
                                    onClick={() => openEditTxModal(t)}
                                  />
                                </Tooltip>
                              )}
                              <Popconfirm
                                title="Удалить транзакцию?"
                                onConfirm={() => handleDeleteTx(t.id)}
                                okText="Да"
                                cancelText="Нет"
                              >
                                <Tooltip title="Удалить">
                                  <Button
                                    icon={<DeleteOutlined />}
                                    size="small"
                                    aria-label="Удалить транзакцию"
                                  />
                                </Tooltip>
                              </Popconfirm>
                            </div>
                          ),
                        },
                      ]}
                    />
                  </>
                ) : (
                  <div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}><Spin /></div>
                )
              )}
            </>
          )}
      </AuthenticatedShell>
      <StockFundamentalsDrawer
        stock={fundamentalsStock}
        open={fundamentalsStock !== null}
        onClose={() => setFundamentalsStock(null)}
      />

      {/* Position Modal */}
      <Modal
        title={editingItem ? 'Редактировать позицию' : 'Добавить позицию'}
        open={posModalOpen}
        onCancel={() => { setPosModalOpen(false); posForm.resetFields(); setEditingItem(null); }}
        footer={null}
      >
        <Form form={posForm} layout="vertical" onFinish={handlePosSubmit}>
          <Form.Item label="Акция" name="stockId" rules={[{ required: true, message: 'Выберите акцию' }]}>
            <Select placeholder="Выберите акцию" showSearch optionFilterProp="label" disabled={!!editingItem}
              options={stocks.map((s) => ({
                value: s.id,
                label: `${s.ticker} [${EXCHANGE_ABBREVIATION[s.exchange] ?? s.exchange}] — ${s.name}`,
              }))}
            />
          </Form.Item>
          <Form.Item label="Количество" name="quantity" rules={[{ required: true, message: 'Введите количество' }]}>
            <InputNumber min={0.01} step={0.01} style={{ width: '100%' }} placeholder="Количество" />
          </Form.Item>
          <Form.Item label="Цена покупки (€)" name="buyPrice" rules={[{ required: true, message: 'Введите цену покупки' }]}>
            <InputNumber min={0} step={0.01} style={{ width: '100%' }} placeholder="Цена покупки" prefix="€" />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={posSubmitting} block>
              {editingItem ? 'Сохранить' : 'Добавить'}
            </Button>
          </Form.Item>
        </Form>
      </Modal>

      {/* Order Modal */}
      <Modal
        title={editingOrder ? 'Редактировать ордер' : 'Создать ордер'}
        open={orderModalOpen}
        onCancel={() => { setOrderModalOpen(false); orderForm.resetFields(); setEditingOrder(null); }}
        footer={null}
        width={520}
      >
        <Form form={orderForm} layout="vertical" onFinish={handleOrderSubmit}>
          <Form.Item label="Акция" name="stockId" rules={[{ required: true, message: 'Выберите акцию' }]}>
            <Select placeholder="Выберите акцию" showSearch optionFilterProp="label" disabled={!!editingOrder}
              options={stocks.map((s) => ({
                value: s.id,
                label: `${s.ticker} [${EXCHANGE_ABBREVIATION[s.exchange] ?? s.exchange}] — ${s.name}`,
              }))}
            />
          </Form.Item>
          <Row gutter={12}>
            <Col span={12}>
              <Form.Item label="Тип" name="type" rules={[{ required: true, message: 'Выберите тип' }]}>
                <Select placeholder="Тип">
                  <Select.Option value="Buy">Покупка</Select.Option>
                  <Select.Option value="Sell">Продажа</Select.Option>
                </Select>
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item label="Статус" name="status" initialValue="Pending">
                <Select>
                  <Select.Option value="Pending">Ожидание</Select.Option>
                  <Select.Option value="Executed">Выполнено</Select.Option>
                  <Select.Option value="Cancelled">Отменено</Select.Option>
                </Select>
              </Form.Item>
            </Col>
          </Row>
          <Row gutter={12}>
            <Col span={12}>
              <Form.Item label="Количество" name="quantity" rules={[{ required: true, message: 'Введите количество' }]}>
                <InputNumber min={0.01} step={0.01} style={{ width: '100%' }} placeholder="Количество" />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item label="Цена (€)" name="price" rules={[{ required: true, message: 'Введите цену' }]}>
                <InputNumber min={0} step={0.01} style={{ width: '100%' }} prefix="€" />
              </Form.Item>
            </Col>
          </Row>
          <Row gutter={12}>
            <Col span={12}>
              <Form.Item label="Stop Loss (€)" name="stopLoss">
                <InputNumber min={0} step={0.01} style={{ width: '100%' }} prefix="€" placeholder="Опционально" />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item label="Stop Market (€)" name="stopMarket">
                <InputNumber min={0} step={0.01} style={{ width: '100%' }} prefix="€" placeholder="Опционально" />
              </Form.Item>
            </Col>
          </Row>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={orderSubmitting} block>
              {editingOrder ? 'Сохранить' : 'Создать'}
            </Button>
          </Form.Item>
        </Form>
      </Modal>

      {/* Transaction Modal */}
      <Modal
        title={editingTx ? 'Редактировать транзакцию' : 'Новая транзакция'}
        open={txModalOpen}
        onCancel={() => { setTxModalOpen(false); txForm.resetFields(); setEditingTx(null); }}
        footer={null}
        width={600}
      >
        <Form form={txForm} layout="vertical" onFinish={handleTxSubmit} initialValues={{ type: 'Deposit', createdAt: dayjs() }}>
          <Form.Item label="Тип" name="type" rules={[{ required: true, message: 'Выберите тип' }]}>
            <Select onChange={(v: TransactionType) => setTxType(v)}>
              <Select.Option value="Deposit">Пополнение</Select.Option>
              <Select.Option value="Withdrawal">Вывод</Select.Option>
              <Select.Option value="Buy">Покупка</Select.Option>
              <Select.Option value="Sell">Продажа</Select.Option>
              <Select.Option value="Dividend">Дивиденды</Select.Option>
            </Select>
          </Form.Item>
          <Form.Item label="Сумма (€)" name="amount" rules={[{ required: true, message: 'Введите сумму' }]}>
            <InputNumber min={0.01} step={0.01} precision={2} style={{ width: '100%' }} prefix="€" placeholder="Положительная сумма" />
          </Form.Item>
          <Form.Item label="Дата" name="createdAt" rules={[{ required: true, message: 'Выберите дату и время' }]}>
            <DatePicker
              showTime={{ format: 'HH:mm' }}
              format="DD.MM.YYYY HH:mm"
              style={{ width: '100%' }}
              placeholder="Выберите дату и время"
            />
          </Form.Item>
          <Form.Item label="Акция" name="stockId">
            <Select
              allowClear
              placeholder="Необязательно"
              showSearch
              optionFilterProp="label"
              options={stockOptions}
              onChange={(stockId: number | undefined) => {
                const stock = stockId != null ? stocks.find((s) => s.id === stockId) : undefined;
                if (stock) {
                  // Only auto-populate if in edit mode and no saved snapshot, or in create mode / auto-derived
                  const isEdit = !!editingTx;
                  const hasSavedSnapshot = isEdit && (editingTx.instrumentCode || editingTx.instrumentCodeType);
                  if (!hasSavedSnapshot || txCodeAutoDerived.current) {
                    const derived = deriveSnapshotFromStock(stock);
                    if (derived) {
                      txForm.setFieldsValue({
                        instrumentCode: derived.instrumentCode,
                        instrumentCodeType: derived.instrumentCodeType,
                      });
                     setTxInstrumentCodeType(derived.instrumentCodeType);
                     txCodeAutoDerived.current = true;
                   }
                 }
                } else {
                 // Cleared stock — clear auto-derived values only
                 if (txCodeAutoDerived.current) {
                   txForm.setFieldsValue({ instrumentCode: undefined, instrumentCodeType: undefined });
                   setTxInstrumentCodeType(null);
                   txCodeAutoDerived.current = false;
                 }
                }
              }}
            />
          </Form.Item>
          <Form.Item label="Описание" name="description">
            <Input placeholder="Необязательно" />
          </Form.Item>
          {/* Instrument snapshot fields — shown for Buy/Sell/Dividend */}
          {(txType === 'Buy' || txType === 'Sell' || txType === 'Dividend') && (
            <>
              <Row gutter={12}>
                <Col xs={24} sm={8}>
                  <Form.Item
                    label="Тип кода"
                    name="instrumentCodeType"
                    dependencies={['instrumentCode']}
                    rules={[
                      {
                        validator(_: unknown, value: unknown) {
                          const code: string | undefined = txForm.getFieldValue('instrumentCode');
                          const hasCode = !!(code && code.trim());
                          if (hasCode && !value) return Promise.reject('Укажите тип кода');
                          if (!hasCode && value) return Promise.reject('Укажите код инструмента');
                          return Promise.resolve();
                        },
                      },
                    ]}
                  >
                    <Select
                      allowClear
                      placeholder="ISIN / Ticker"
                      onChange={(v: 'ISIN' | 'Ticker' | undefined) => {
                        setTxInstrumentCodeType(v ?? null);
                        // Re-validate instrumentCode so paired errors clear
                        txForm.validateFields(['instrumentCode']);
                      }}
                    >
                      <Select.Option value="ISIN">ISIN</Select.Option>
                      <Select.Option value="Ticker">Ticker</Select.Option>
                    </Select>
                  </Form.Item>
                </Col>
                <Col xs={24} sm={16}>
                  <Form.Item
                    label="Код инструмента"
                    name="instrumentCode"
                    dependencies={['instrumentCodeType']}
                    rules={[
                      {
                        validator(_: unknown, value: unknown) {
                          const codeType: InstrumentCodeType | undefined = txForm.getFieldValue('instrumentCodeType');
                          const code = typeof value === 'string' ? value.trim() : '';
                          if (codeType && !code) return Promise.reject('Укажите код инструмента');
                          if (!codeType && code) return Promise.reject('Укажите тип кода');
                          if (codeType === 'ISIN' && code && !isValidIsin(code)) {
                            return Promise.reject('Неверный формат ISIN (12 символов: CC + 9 буквенно-цифровых + цифра)');
                          }
                          if (codeType === 'Ticker' && code && !isValidTicker(code)) {
                            return Promise.reject('Тикер не может быть пустым или длиннее 32 символов');
                          }
                          return Promise.resolve();
                        },
                      },
                    ]}
                  >
                    <Input
                      placeholder="Необязательно"
                      maxLength={txInstrumentCodeType === 'ISIN' ? 12 : 32}
                      onChange={() => {
                        // Re-validate instrumentCodeType so paired errors clear
                        txForm.validateFields(['instrumentCodeType']);
                      }}
                    />
                  </Form.Item>
                </Col>
              </Row>
              {(txType === 'Buy' || txType === 'Sell') && (
                <Row gutter={12}>
                  <Col xs={24} sm={12}>
                    <Form.Item
                      label="Количество"
                      name="quantity"
                      rules={[
                        {
                          validator(_: unknown, value: unknown) {
                            if (value == null) return Promise.resolve();
                            if ((value as number) < 0) return Promise.reject('Количество не может быть отрицательным');
                            return Promise.resolve();
                          },
                        },
                      ]}
                    >
                      <InputNumber min={0} step={0.00000001} precision={8} style={{ width: '100%' }} placeholder="Необязательно" />
                    </Form.Item>
                  </Col>
                  <Col xs={24} sm={12}>
                    <Form.Item
                      label="Цена за единицу (€)"
                      name="unitPrice"
                      rules={[
                        {
                          validator(_: unknown, value: unknown) {
                            if (value == null) return Promise.resolve();
                            if ((value as number) < 0) return Promise.reject('Цена не может быть отрицательной');
                            return Promise.resolve();
                          },
                        },
                      ]}
                    >
                      <InputNumber min={0} step={0.00000001} precision={8} style={{ width: '100%' }} prefix="€" placeholder="Необязательно" />
                    </Form.Item>
                  </Col>
                </Row>
              )}
            </>
          )}
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={txSubmitting} block>
              {editingTx ? 'Сохранить' : 'Добавить'}
            </Button>
          </Form.Item>
        </Form>
      </Modal>
    </>
  );
};

export default PortfolioDetailPage;
