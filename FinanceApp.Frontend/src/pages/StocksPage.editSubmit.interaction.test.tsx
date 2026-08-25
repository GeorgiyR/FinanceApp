// @vitest-environment jsdom
import React from 'react';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Modal } from 'antd';
import AuthContext from '../contexts/AuthContext';
import type { Stock } from '../types';
import StocksPage from './StocksPage';

const baseStock: Stock = {
  id: 11,
  ticker: 'ALMTF',
  name: 'Almonty Industries',
  commonName: 'Almonty',
  exchange: 'NYSE',
  currentPrice: 10,
  updatedAt: '2026-08-25T00:00:00Z',
  trackingStatus: 1,
  wkn: null,
  isin: null,
  finanzenNetSlug: null,
  marketIndexIds: [],
};

const makeStock = (patch: Partial<Stock> = {}): Stock => ({ ...baseStock, ...patch });

vi.mock('../services/api', async () => {
  const actual = await vi.importActual<typeof import('../services/api')>('../services/api');
  return {
    ...actual,
    getTrackedStocks: vi.fn(),
    getStockCatalog: vi.fn(),
    getPortfolios: vi.fn().mockResolvedValue({ data: [] }),
    getSectors: vi.fn().mockResolvedValue([]),
    getMarketIndices: vi.fn().mockResolvedValue([]),
    getStockCatalogPerformance: vi.fn().mockResolvedValue({ data: { range: '1m', period: '1m', generatedAtUtc: null, items: [] } }),
    updateStockEdit: vi.fn(),
    createStock: vi.fn(),
    updateStockQuote: vi.fn(),
    getStockPrice: vi.fn(),
    trackStock: vi.fn(),
    untrackStock: vi.fn(),
    deleteStockPermanent: vi.fn(),
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

const openEditModal = async () => {
  await waitFor(() => expect(screen.getByText('ALMTF')).toBeInTheDocument());
  await userEvent.click(screen.getByRole('button', { name: 'Изменить' }));
  await screen.findByText('Редактировать акцию');
};

const queueConfirmHandlers = (handlers: Array<'ok' | 'cancel'>) => {
  const spy = vi.spyOn(Modal, 'confirm').mockImplementation((config) => {
    const next = handlers.shift();
    if (next === 'ok') {
      void config.onOk?.();
    } else {
      void config.onCancel?.();
    }
    return { destroy: () => {}, update: () => {} } as never;
  });
  return spy;
};

describe('StocksPage stock edit submit interactions', () => {
  beforeEach(async () => {
    vi.clearAllMocks();
    const api = await import('../services/api');
    vi.mocked(api.getTrackedStocks).mockResolvedValue({ data: [makeStock()] });
    vi.mocked(api.getStockCatalog).mockResolvedValue({ data: [makeStock({ trackingStatus: 0 })] });
    vi.mocked(api.updateStockEdit).mockResolvedValue({ data: undefined });
    vi.mocked(api.deleteStockPermanent).mockResolvedValue({ data: undefined });
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it.each(['tracked', 'catalog'] as const)('submits metadata-only edit once in %s mode and refreshes data', async (mode) => {
    const api = await import('../services/api');
    const updatedStock = makeStock({ name: 'Almonty Updated', trackingStatus: mode === 'catalog' ? 0 : 1 });

    if (mode === 'tracked') {
      vi.mocked(api.getTrackedStocks)
        .mockResolvedValueOnce({ data: [makeStock()] })
        .mockResolvedValueOnce({ data: [updatedStock] });
    } else {
      vi.mocked(api.getStockCatalog)
        .mockResolvedValueOnce({ data: [makeStock({ trackingStatus: 0 })] })
        .mockResolvedValueOnce({ data: [updatedStock] });
    }

    renderPage(mode);
    await openEditModal();

    const nameInput = screen.getByLabelText('Название');
    await userEvent.clear(nameInput);
    await userEvent.type(nameInput, 'Almonty Updated');

    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(vi.mocked(api.updateStockEdit)).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.updateStockEdit).mock.calls[0]?.[1]).toMatchObject({
      name: 'Almonty Updated',
      ticker: 'ALMTF',
      exchange: 'NYSE',
      identityEditingEnabled: false,
      confirmationText: '',
      wkn: null,
      isin: null,
    });

    await waitFor(() => expect(screen.getByText('Almonty Updated')).toBeInTheDocument());
  });

  it('does not block submit when WKN and ISIN are empty', async () => {
    const api = await import('../services/api');

    renderPage('tracked');
    await openEditModal();

    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(vi.mocked(api.updateStockEdit)).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.updateStockEdit).mock.calls[0]?.[1]).toMatchObject({ wkn: null, isin: null });
  });

  it('blocks invalid submit, keeps request unsent, and shows visible validation feedback near Save', async () => {
    const api = await import('../services/api');

    renderPage('tracked');
    await openEditModal();

    const nameInput = screen.getByLabelText('Название');
    await userEvent.clear(nameInput);
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    expect(vi.mocked(api.updateStockEdit)).not.toHaveBeenCalled();
    expect(await screen.findByText('Проверьте обязательные поля и исправьте ошибки формы.')).toBeInTheDocument();
    expect(nameInput).toHaveFocus();
  });

  it('shows identity transition confirmation and sends one atomic edit request after acceptance', async () => {
    const api = await import('../services/api');
    const confirmSpy = queueConfirmHandlers(['ok', 'ok']);

    renderPage('tracked');
    await openEditModal();

    await userEvent.click(screen.getByRole('button', { name: 'Изменить тикер / биржу' }));

    const tickerInput = screen.getByPlaceholderText('AAPL');
    await userEvent.clear(tickerInput);
    await userEvent.type(tickerInput, 'ALMNEW');

    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(vi.mocked(api.updateStockEdit)).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.updateStockEdit).mock.calls[0]?.[1]).toMatchObject({
      ticker: 'ALMNEW',
      exchange: 'NYSE',
      identityEditingEnabled: true,
      confirmationText: 'ALMTF (NYSE) → ALMNEW (NYSE)',
    });
    expect(confirmSpy).toHaveBeenCalledTimes(2);
  });

  it('keeps transition confirmation cancel safe: guard releases and retry succeeds', async () => {
    const api = await import('../services/api');
    const confirmSpy = queueConfirmHandlers(['ok', 'cancel', 'ok']);

    renderPage('tracked');
    await openEditModal();

    await userEvent.click(screen.getByRole('button', { name: 'Изменить тикер / биржу' }));

    const tickerInput = screen.getByPlaceholderText('AAPL');
    await userEvent.clear(tickerInput);
    await userEvent.type(tickerInput, 'ALMTRY');

    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(vi.mocked(api.updateStockEdit)).toHaveBeenCalledTimes(0));

    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));
    await waitFor(() => expect(vi.mocked(api.updateStockEdit)).toHaveBeenCalledTimes(1));
    expect(confirmSpy).toHaveBeenCalledTimes(3);
  });

  it.each([
    {
      scenario: 'plain 400',
      error: { isAxiosError: true, response: { status: 400, data: 'Неверный формат подтверждения' } },
      expected: /Неверный формат подтверждения/i,
    },
    {
      scenario: 'validation problem details',
      error: {
        isAxiosError: true,
        response: {
          status: 400,
          data: {
            title: 'Validation failed',
            errors: { ticker: ['Ticker is invalid.'] },
          },
        },
      },
      expected: /Validation failed: Ticker is invalid\./i,
    },
    {
      scenario: 'structured 409',
      error: {
        isAxiosError: true,
        response: {
          status: 409,
          data: {
            message: 'Изменение заблокировано зависимостями.',
            diagnostics: { stockId: 11, hasBlockers: true, blockers: [] },
          },
        },
      },
      expected: /Изменение заблокировано зависимостями\./i,
    },
    {
      scenario: 'network error',
      error: { isAxiosError: true, response: undefined },
      expected: /Не удалось отправить запрос/i,
    },
    {
      scenario: 'server 500',
      error: { isAxiosError: true, response: { status: 500, data: { title: 'Internal Server Error' } } },
      expected: /Internal Server Error|Ошибка сервера \(500\)/i,
    },
  ])('renders %s visibly and keeps edited values for retry', async ({ scenario, error, expected }) => {
    const api = await import('../services/api');
    vi.mocked(api.updateStockEdit).mockRejectedValueOnce(error as never);

    renderPage('tracked');
    await openEditModal();

    const nameInput = screen.getByLabelText('Название');
    await userEvent.clear(nameInput);
    await userEvent.type(nameInput, `Edited ${scenario}`);
    await userEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    expect((await screen.findAllByText(expected)).length).toBeGreaterThan(0);
    expect(screen.getByLabelText('Название')).toHaveValue(`Edited ${scenario}`);
  });

  it('prevents duplicate in-flight submits', async () => {
    const api = await import('../services/api');

    let resolveUpdate: (() => void) | null = null;
    vi.mocked(api.updateStockEdit).mockImplementation(() => new Promise((resolve) => {
      resolveUpdate = () => resolve({ data: undefined } as never);
    }));

    renderPage('tracked');
    await openEditModal();

    const saveButton = screen.getByRole('button', { name: 'Сохранить' });
    await userEvent.click(saveButton);
    await userEvent.click(saveButton);

    await waitFor(() => expect(vi.mocked(api.updateStockEdit)).toHaveBeenCalledTimes(1));
    resolveUpdate?.();
  });

  it('shows permanent delete only inside edit modal and confirms deletion without typed input', async () => {
    const api = await import('../services/api');

    renderPage('catalog');
    await waitFor(() => expect(screen.getByText('ALMTF')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Удалить акцию полностью' })).not.toBeInTheDocument();

    await openEditModal();

    const editDialog = screen.getByRole('dialog', { name: 'Редактировать акцию' });
    const permanentDeleteButton = within(editDialog).getByRole('button', { name: 'Удалить акцию полностью' });
    expect(permanentDeleteButton).toBeInTheDocument();
    await userEvent.click(permanentDeleteButton);

    const confirmTitle = await screen.findByText('Удалить акцию полностью?');
    const confirmModal = confirmTitle.closest('.ant-modal');
    expect(confirmModal).not.toBeNull();
    expect(within(confirmModal as HTMLElement).queryByRole('textbox')).not.toBeInTheDocument();
    await userEvent.click(within(confirmModal as HTMLElement).getByRole('button', { name: 'Удалить' }));

    await waitFor(() => expect(vi.mocked(api.deleteStockPermanent)).toHaveBeenCalledTimes(1));
  });

  it('does not delete permanently when cancellation is chosen in confirmation dialog', async () => {
    const api = await import('../services/api');

    renderPage('catalog');
    await openEditModal();

    const editDialog = screen.getByRole('dialog', { name: 'Редактировать акцию' });
    await userEvent.click(within(editDialog).getByRole('button', { name: 'Удалить акцию полностью' }));

    const confirmTitle = await screen.findByText('Удалить акцию полностью?');
    const confirmModal = confirmTitle.closest('.ant-modal');
    expect(confirmModal).not.toBeNull();
    await userEvent.click(within(confirmModal as HTMLElement).getByRole('button', { name: 'Отмена' }));

    await waitFor(() => expect(vi.mocked(api.deleteStockPermanent)).toHaveBeenCalledTimes(0));
  });
});
