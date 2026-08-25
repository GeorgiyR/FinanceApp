import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { Button, Popconfirm, Tooltip } from 'antd';
import {
  getStockDeleteErrorMessage,
  PROTECTED_STOCK_DELETE_TOOLTIP,
  STOCK_DELETE_TOOLTIP,
  StockDeleteAction,
  IDENTITY_IMMUTABLE_HELPER,
} from './StocksPage';

const getProtectedDeleteButton = (node: React.ReactElement) => {
  const tooltip = node;
  const span = tooltip.props.children as React.ReactElement;
  return span.props.children as React.ReactElement;
};

const getUnprotectedDeleteButton = (node: React.ReactElement) => {
  const popconfirm = node;
  const tooltip = popconfirm.props.children as React.ReactElement;
  const span = tooltip.props.children as React.ReactElement;
  return span.props.children as React.ReactElement;
};

describe('StockDeleteAction', () => {
  it('disables delete for portfolio stocks and shows explanatory tooltip without Popconfirm', () => {
    const onDelete = vi.fn();
    const element = StockDeleteAction({ isProtected: true, onDelete }) as React.ReactElement;

    expect(element.type).toBe(Tooltip);
    expect(element.props.title).toBe(PROTECTED_STOCK_DELETE_TOOLTIP);

    const button = getProtectedDeleteButton(element);
    expect(button.type).toBe(Button);
    expect(button.props.disabled).toBe(true);
    expect(button.props['aria-label']).toBe('Удалить из отслеживаемых');
    button.props.onClick?.();
    expect(onDelete).not.toHaveBeenCalled();
  });

  it('keeps Popconfirm flow for unprotected stocks and allows deletion', () => {
    const onDelete = vi.fn();
    const element = StockDeleteAction({ isProtected: false, onDelete }) as React.ReactElement;

    expect(element.type).toBe(Popconfirm);
    expect(element.props.title).toBe('Удалить из отслеживаемых? Акция останется в «Список акций», индексах и портфелях.');

    const tooltip = element.props.children as React.ReactElement;
    expect(tooltip.type).toBe(Tooltip);
    expect(tooltip.props.title).toBe(STOCK_DELETE_TOOLTIP);

    const button = getUnprotectedDeleteButton(element);
    expect(button.props.disabled).toBe(false);

    element.props.onConfirm();
    expect(onDelete).toHaveBeenCalledTimes(1);
  });
});

describe('getStockDeleteErrorMessage', () => {
  it('returns textual backend conflict message when present', () => {
    const result = getStockDeleteErrorMessage({
      isAxiosError: true,
      response: {
        status: 409,
        data: 'Невозможно удалить акцию: она используется как минимум в одном портфеле.',
      },
    });

    expect(result).toBe('Невозможно удалить акцию: она используется как минимум в одном портфеле.');
  });

  it('falls back to generic delete error for unknown failures', () => {
    expect(getStockDeleteErrorMessage(new Error('boom'))).toBe('Ошибка удаления из отслеживаемых');
  });
});


describe('identity immutability in edit mode', () => {
  it('exports IDENTITY_IMMUTABLE_HELPER explaining why ticker/exchange cannot be changed', () => {
    expect(IDENTITY_IMMUTABLE_HELPER).toContain('Тикер');
    expect(IDENTITY_IMMUTABLE_HELPER).toContain('биржа');
  });

  it('IDENTITY_IMMUTABLE_HELPER instructs user to create a new stock for a different ticker/exchange', () => {
    expect(IDENTITY_IMMUTABLE_HELPER.toLowerCase()).toContain('создайте новую акцию');
  });
});
