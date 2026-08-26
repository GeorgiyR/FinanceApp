// @vitest-environment jsdom
import React from 'react';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import AdvancedStockFiltersDrawer from './AdvancedStockFiltersDrawer';
import { EMPTY_ADVANCED_STOCK_FILTERS } from './stockFilterModel';

const renderDrawer = (overrides?: Partial<React.ComponentProps<typeof AdvancedStockFiltersDrawer>>) => {
  const onClose = vi.fn();
  const onDraftChange = vi.fn();
  const onClearDraft = vi.fn();
  const onApply = vi.fn();

  const view = render(
    <AdvancedStockFiltersDrawer
      open
      draftFilters={EMPTY_ADVANCED_STOCK_FILTERS}
      sectorOptions={[]}
      industryOptions={[]}
      onClose={onClose}
      onDraftChange={onDraftChange}
      onClearDraft={onClearDraft}
      onApply={onApply}
      {...overrides}
    />,
  );

  return { ...view, onClose, onDraftChange, onClearDraft, onApply };
};

describe('AdvancedStockFiltersDrawer interactions', () => {
  afterEach(() => {
    cleanup();
  });

  it('renders compact header action buttons and no footer text actions', () => {
    const { container } = renderDrawer();

    expect(screen.getByText('Расширенные фильтры')).toBeInTheDocument();
    const applyButton = screen.getByRole('button', { name: 'Применить фильтры' });
    const clearButton = screen.getByRole('button', { name: 'Очистить фильтры' });
    expect(applyButton).toBeInTheDocument();
    expect(clearButton).toBeInTheDocument();
    expect(applyButton).toHaveClass('ant-btn');
    expect(clearButton).toHaveClass('ant-btn');
    expect(applyButton).not.toHaveClass('ant-btn-text');
    expect(clearButton).not.toHaveClass('ant-btn-text');
    expect(applyButton.className).toMatch(/ant-btn-(primary|color-primary)/);
    expect(clearButton.className).toMatch(/ant-btn-(dangerous|color-dangerous)/);
    expect(clearButton.className).toMatch(/ant-btn-(default|variant-outlined)/);

    expect(screen.queryByRole('button', { name: 'Применить' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Очистить' })).not.toBeInTheDocument();
    expect(container.querySelector('.ant-drawer-footer')).toBeNull();
  });

  it('keeps separate right-aligned action group after title in DOM order', () => {
    renderDrawer();

    const header = document.querySelector('.ant-drawer-header');
    const title = screen.getByText('Расширенные фильтры');
    const applyButton = screen.getByRole('button', { name: 'Применить фильтры' });
    const clearButton = screen.getByRole('button', { name: 'Очистить фильтры' });
    const actionsGroup = screen.getByTestId('advanced-filters-header-actions');

    expect(header).not.toBeNull();
    expect(header).toContainElement(title);
    expect(header).toContainElement(applyButton);
    expect(header).toContainElement(clearButton);

    const titleContainer = title.closest('.ant-drawer-title');
    expect(titleContainer).not.toBeNull();
    expect(titleContainer).toContainElement(applyButton);
    expect(titleContainer).toContainElement(clearButton);
    expect(actionsGroup).toContainElement(applyButton);
    expect(actionsGroup).toContainElement(clearButton);
    expect(actionsGroup).not.toContainElement(title);
    expect(actionsGroup).toHaveStyle({ marginLeft: 'auto', flexShrink: '0' });

    expect(title.compareDocumentPosition(applyButton) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(applyButton.compareDocumentPosition(clearButton) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('applies draft once via check icon and supports keyboard activation', async () => {
    const user = userEvent.setup();
    const { onApply } = renderDrawer();
    const applyButton = screen.getByRole('button', { name: 'Применить фильтры' });

    await user.click(applyButton);
    expect(onApply).toHaveBeenCalledTimes(1);

    applyButton.focus();
    await user.keyboard('{Enter}');
    expect(onApply).toHaveBeenCalledTimes(2);
  });

  it('clears draft once without applying or closing and supports keyboard activation', async () => {
    const user = userEvent.setup();
    const { onClearDraft, onApply, onClose } = renderDrawer();
    const clearButton = screen.getByRole('button', { name: 'Очистить фильтры' });

    await user.click(clearButton);
    expect(onClearDraft).toHaveBeenCalledTimes(1);
    expect(onApply).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    clearButton.focus();
    await user.keyboard('{Enter}');
    expect(onClearDraft).toHaveBeenCalledTimes(2);
    expect(onApply).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('standard drawer close control only closes and does not apply or clear', async () => {
    const user = userEvent.setup();
    const { onClose, onApply, onClearDraft } = renderDrawer();
    const closeControl = document.querySelector('.ant-drawer-close');

    expect(closeControl).not.toBeNull();

    await user.click(closeControl as HTMLElement);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(onApply).not.toHaveBeenCalled();
    expect(onClearDraft).not.toHaveBeenCalled();
  });
});
