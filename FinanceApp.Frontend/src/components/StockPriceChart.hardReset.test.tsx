// @vitest-environment jsdom
import React from 'react';
import userEvent from '@testing-library/user-event';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import StockPriceChart from './StockPriceChart';
import * as api from '../services/api';

vi.mock('../services/api', () => ({
  getStockHistory: vi.fn(),
  refreshStockHistory: vi.fn(),
  getIndexConstituentHistory: vi.fn(),
  getStockHistoryRoutingDiagnostics: vi.fn(),
  validateStockHistoryProviderSymbol: vi.fn(),
  hardResetStockHistory: vi.fn(),
}));

vi.mock('./StockTechnicalAnalysisPanel', () => ({
  default: () => <div data-testid="technical-analysis-panel" />,
}));

const historyResponse = {
  range: '1y',
  interval: '1wk',
  currency: 'USD',
  financialCurrency: 'USD',
  normalizedQuoteCurrency: 'USD',
  quoteUnitMultiplier: 1,
  rateToEur: null,
  rateTimestampUtc: null,
  rateSource: null,
  conversionWarning: null,
  asOfUtc: '2026-08-20T14:40:00Z',
  currentSessionHasCandles: true,
  isPotentiallyStale: false,
  staleReason: null,
  unavailableReason: null,
  volumeMetrics: {
    averageVolume20: null,
    averageVolume50: null,
    relativeVolume: null,
    turnover: null,
    turnoverCurrency: null,
    latestMetricsTimestamp: null,
    usesCompletedCandle: true,
  },
  points: [
    {
      timestamp: '2026-08-20T14:40:00Z',
      interval: '1wk',
      openRaw: 100,
      highRaw: 101,
      lowRaw: 99,
      closeRaw: 100,
      openNormalized: 100,
      highNormalized: 101,
      lowNormalized: 99,
      closeNormalized: 100,
      openEur: null,
      highEur: null,
      lowEur: null,
      closeEur: null,
      volume: 1000,
      isQuoteDerived: false,
    },
  ],
} as const;

const diagnostics = {
  stockId: 1,
  ticker: 'SMEGF',
  exchange: 'Frankfurt',
  name: 'Siemens Energy AG',
  configuredProviderSymbol: 'SMEGF',
  effectiveProviderSymbol: 'SMEGF',
  candidateOverrideSymbol: null,
  provider: 'yahoo',
  resultBucket: 'success',
  providerQuoteSymbol: 'SMEGF',
  providerCurrency: 'USD',
  providerPriceTimestampUtc: '2026-08-20T14:40:00Z',
  retryAfterSeconds: null,
  deletedRows: 0,
  insertedRows: 0,
  finalRows: 0,
  resetPerformed: false,
  intervals: [],
  warnings: [],
  errors: [],
} as const;

describe('StockPriceChart hard reset workflow', () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('requires successful validation and typed confirmation before destructive reset', async () => {
    vi.mocked(api.getStockHistory).mockResolvedValue({ data: historyResponse } as never);
    vi.mocked(api.getStockHistoryRoutingDiagnostics).mockResolvedValue({ data: diagnostics } as never);
    vi.mocked(api.validateStockHistoryProviderSymbol).mockResolvedValue({
      data: { ...diagnostics, resultBucket: 'success', candidateOverrideSymbol: 'ENR.DE' },
    } as never);
    vi.mocked(api.hardResetStockHistory).mockResolvedValue({
      data: { ...diagnostics, resetPerformed: true, resultBucket: 'success', deletedRows: 10, insertedRows: 22, finalRows: 22 },
    } as never);

    render(<StockPriceChart panelId="p1" stockId={1} ticker="SMEGF" name="Siemens Energy AG" exchange="Frankfurt" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalled());

    await userEvent.click(screen.getByRole('button', { name: 'Полностью восстановить историю' }));
    await waitFor(() => expect(vi.mocked(api.getStockHistoryRoutingDiagnostics)).toHaveBeenCalledWith(1));

    const dialog = await screen.findByRole('dialog');
    await userEvent.clear(within(dialog).getByPlaceholderText('Кандидат ProviderSymbol (например ENR.DE)'));
    await userEvent.type(within(dialog).getByPlaceholderText('Кандидат ProviderSymbol (например ENR.DE)'), 'ENR.DE');
    const validateButton = within(dialog).getByRole('button', { name: /Проверить символ/i });
    await waitFor(() => expect(validateButton).toBeEnabled());
    await userEvent.click(validateButton);
    await waitFor(() => expect(vi.mocked(api.validateStockHistoryProviderSymbol)).toHaveBeenCalledWith(1, 'ENR.DE'));

    const resetButton = within(dialog).getByRole('button', { name: 'Полностью восстановить историю' });
    expect(resetButton).toBeDisabled();
    await userEvent.type(within(dialog).getByPlaceholderText('Введите "SMEGF" или "УДАЛИТЬ"'), 'SMEGF');
    expect(resetButton).toBeEnabled();
    await userEvent.click(resetButton);

    await waitFor(() => expect(vi.mocked(api.hardResetStockHistory)).toHaveBeenCalledWith(1, 'SMEGF', 'ENR.DE'));
  });

  it('cannot run reset before validation succeeds', async () => {
    vi.mocked(api.getStockHistory).mockResolvedValue({ data: historyResponse } as never);
    vi.mocked(api.getStockHistoryRoutingDiagnostics).mockResolvedValue({ data: diagnostics } as never);

    render(<StockPriceChart panelId="p2" stockId={1} ticker="SMEGF" name="Siemens Energy AG" exchange="Frankfurt" />);
    await waitFor(() => expect(vi.mocked(api.getStockHistory)).toHaveBeenCalled());

    await userEvent.click(screen.getByRole('button', { name: 'Полностью восстановить историю' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.type(within(dialog).getByPlaceholderText('Введите "SMEGF" или "УДАЛИТЬ"'), 'УДАЛИТЬ');

    const resetButton = within(dialog).getByRole('button', { name: 'Полностью восстановить историю' });
    expect(resetButton).toBeDisabled();
    expect(vi.mocked(api.hardResetStockHistory)).not.toHaveBeenCalled();
  });
});
