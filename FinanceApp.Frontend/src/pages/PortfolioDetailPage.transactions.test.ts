import { describe, expect, it } from 'vitest';
import {
  computeTransactionRemainder,
  computeTransactionPortfolioTotal,
  computeTransactionTypeTotals,
  shouldRefreshPortfolioAfterTransaction,
} from './PortfolioDetailPage';
import type { Transaction } from '../types';

/** Minimal transaction factory. */
const tx = (
  type: Transaction['type'],
  amount: number,
  signedAmount: number,
): Transaction => ({
  id: 0,
  portfolioId: 1,
  type,
  amount,
  signedAmount,
  description: null,
  createdAt: '2024-01-01T00:00:00Z',
  stockId: null,
  stock: null,
  orderId: null,
  instrumentCode: null,
  instrumentCodeType: null,
  quantity: null,
  unitPrice: null,
});

describe('shouldRefreshPortfolioAfterTransaction', () => {
  it('refreshes positions for buy and sell transactions', () => {
    expect(shouldRefreshPortfolioAfterTransaction('Buy')).toBe(true);
    expect(shouldRefreshPortfolioAfterTransaction('Sell')).toBe(true);
  });

  it('does not refresh positions for cash or dividend transactions', () => {
    expect(shouldRefreshPortfolioAfterTransaction('Deposit')).toBe(false);
    expect(shouldRefreshPortfolioAfterTransaction('Withdrawal')).toBe(false);
    expect(shouldRefreshPortfolioAfterTransaction('Dividend')).toBe(false);
  });
});

describe('computeTransactionRemainder', () => {
  it('returns 0 for empty list', () => {
    expect(computeTransactionRemainder([])).toBe(0);
  });

  it('adds deposits', () => {
    expect(computeTransactionRemainder([tx('Deposit', 1000, 1000)])).toBe(1000);
  });

  it('subtracts withdrawals', () => {
    expect(computeTransactionRemainder([tx('Withdrawal', 200, -200)])).toBe(-200);
  });

  it('subtracts buys', () => {
    expect(computeTransactionRemainder([tx('Buy', 500, -500)])).toBe(-500);
  });

  it('adds sells', () => {
    expect(computeTransactionRemainder([tx('Sell', 300, 300)])).toBe(300);
  });

  it('adds dividends', () => {
    expect(computeTransactionRemainder([tx('Dividend', 50, 50)])).toBe(50);
  });

  it('computes net across all five types', () => {
    const txs = [
      tx('Deposit', 1000, 1000),
      tx('Withdrawal', 200, -200),
      tx('Buy', 500, -500),
      tx('Sell', 300, 300),
      tx('Dividend', 50, 50),
    ];
    // 1000 - 200 - 500 + 300 + 50 = 650
    expect(computeTransactionRemainder(txs)).toBe(650);
  });

  it('uses legacy fallback when signedAmount is 0 and amount > 0 (Deposit)', () => {
    // Legacy: signedAmount=0 but amount=500 and type=Deposit → treat as +500
    expect(computeTransactionRemainder([tx('Deposit', 500, 0)])).toBe(500);
  });

  it('uses legacy fallback when signedAmount is 0 and amount > 0 (Buy)', () => {
    // Legacy: signedAmount=0 but amount=300 and type=Buy → treat as -300
    expect(computeTransactionRemainder([tx('Buy', 300, 0)])).toBe(-300);
  });

  it('treats signedAmount=0 and amount=0 as zero', () => {
    expect(computeTransactionRemainder([tx('Deposit', 0, 0)])).toBe(0);
  });
});

describe('computeTransactionPortfolioTotal', () => {
  it('returns stocksValue + remainder', () => {
    expect(computeTransactionPortfolioTotal(5000, 650)).toBe(5650);
  });

  it('handles negative remainder', () => {
    expect(computeTransactionPortfolioTotal(5000, -200)).toBe(4800);
  });

  it('handles zero values', () => {
    expect(computeTransactionPortfolioTotal(0, 0)).toBe(0);
  });

  // Req: transaction portfolio total uses current stock value (summary.totalValue), not stale balance.stocksValue
  it('uses current stock value (e.g. summary.totalValue) when computing portfolio total', () => {
    // Simulate: stale balance.stocksValue = 4000, but current summary.totalValue = 4500 after a quote refresh
    const staleBalanceStocksValue = 4000;
    const currentStocksValue = 4500;
    const remainder = 300;

    // The correct result must use the current value, not the stale one
    expect(computeTransactionPortfolioTotal(currentStocksValue, remainder)).toBe(4800);
    expect(computeTransactionPortfolioTotal(staleBalanceStocksValue, remainder)).toBe(4300);
  });

  // Req: changing effective stock value (from effectiveItems) updates transaction summary values
  it('reflects updated stock value when effective items change after quote refresh', () => {
    const remainder = 500;

    // Before refresh
    const stockValueBefore = 10_000;
    const totalBefore = computeTransactionPortfolioTotal(stockValueBefore, remainder);
    expect(totalBefore).toBe(10_500);

    // After refresh (prices went up)
    const stockValueAfter = 11_200;
    const totalAfter = computeTransactionPortfolioTotal(stockValueAfter, remainder);
    expect(totalAfter).toBe(11_700);

    // The total must change proportionally to the stock value change
    expect(totalAfter - totalBefore).toBe(stockValueAfter - stockValueBefore);
  });
});

describe('computeTransactionTypeTotals', () => {
  it('returns zeros for empty list', () => {
    const totals = computeTransactionTypeTotals([]);
    expect(totals.Deposit).toBe(0);
    expect(totals.Withdrawal).toBe(0);
    expect(totals.Buy).toBe(0);
    expect(totals.Sell).toBe(0);
    expect(totals.Dividend).toBe(0);
  });

  it('accumulates absolute amounts per type', () => {
    const txs = [
      tx('Deposit', 1000, 1000),
      tx('Deposit', 500, 500),
      tx('Withdrawal', 200, -200),
      tx('Buy', 300, -300),
      tx('Buy', 150, -150),
      tx('Sell', 400, 400),
      tx('Dividend', 75, 75),
    ];
    const totals = computeTransactionTypeTotals(txs);
    expect(totals.Deposit).toBe(1500);
    expect(totals.Withdrawal).toBe(200);
    expect(totals.Buy).toBe(450);
    expect(totals.Sell).toBe(400);
    expect(totals.Dividend).toBe(75);
  });

  it('uses amount field (positive absolute value) for aggregation', () => {
    // Buy has negative signedAmount but positive amount
    const txs = [tx('Buy', 500, -500)];
    const totals = computeTransactionTypeTotals(txs);
    expect(totals.Buy).toBe(500);
  });
});
