// @vitest-environment jsdom
import React from 'react';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { Table } from 'antd';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import SystemProcessRunsPage from './SystemProcessRunsPage';

const getPortfoliosMock = vi.fn();
const getSystemProcessRunsMock = vi.fn();
const getSystemProcessRunsSummaryMock = vi.fn();
const getSystemProcessRunMock = vi.fn();

vi.mock('../services/api', () => ({
  getPortfolios: (...args: unknown[]) => getPortfoliosMock(...args),
  getSystemProcessRuns: (...args: unknown[]) => getSystemProcessRunsMock(...args),
  getSystemProcessRunsSummary: (...args: unknown[]) => getSystemProcessRunsSummaryMock(...args),
  getSystemProcessRun: (...args: unknown[]) => getSystemProcessRunMock(...args),
}));

vi.mock('../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { username: 'tester' },
    logout: vi.fn(),
  }),
}));

vi.mock('../components/AuthenticatedShell', () => ({
  default: ({
    children,
    headerLeft,
    headerRight,
  }: {
    children: React.ReactNode;
    headerLeft?: React.ReactNode;
    headerRight?: React.ReactNode;
  }) => (
    <div>
      <div>{headerLeft}</div>
      <div>{headerRight}</div>
      {children}
    </div>
  ),
}));

const defaultRun = {
  id: 1,
  processType: 'stock-quote-refresh-cycle',
  displayName: 'Обновление каталога акций',
  status: 'Running',
  trigger: 'Automatic',
  queuedAtUtc: '2026-08-25T09:00:00Z',
  startedAtUtc: '2026-08-25T09:00:05Z',
  completedAtUtc: null,
  updatedAtUtc: '2026-08-25T09:01:00Z',
  correlationId: 'corr-1',
  totalItems: 100,
  processedItems: 25,
  succeededItems: 25,
  failedItems: 0,
  skippedItems: 0,
  resultSummary: null,
  errorSummary: null,
  durationSeconds: 55,
  progressPercent: 25,
};

const renderPage = () => render(
  <MemoryRouter>
    <SystemProcessRunsPage />
  </MemoryRouter>,
);

describe('SystemProcessRunsPage', () => {
  const originalTz = process.env.TZ;

  beforeAll(() => {
    process.env.TZ = 'UTC';
  });

  afterAll(() => {
    process.env.TZ = originalTz;
  });

  beforeEach(() => {
    vi.clearAllMocks();
    getPortfoliosMock.mockResolvedValue({ data: [] });
    getSystemProcessRunMock.mockResolvedValue({ data: {} });
  });

  afterEach(() => {
    cleanup();
  });

  it('renders valid enum labels for status and trigger', async () => {
    getSystemProcessRunsMock.mockResolvedValue({
      data: {
        page: 1,
        pageSize: 50,
        totalCount: 1,
        serverNowUtc: '2026-08-25T09:01:00Z',
        items: [defaultRun],
      },
    });
    getSystemProcessRunsSummaryMock.mockResolvedValue({ data: { activeCount: 1 } });

    renderPage();

    expect(await screen.findByText('Выполняется')).toBeInTheDocument();
    expect(screen.getByText('Авто.')).toBeInTheDocument();
    expect(screen.queryByText('Автоматически')).not.toBeInTheDocument();
    expect(screen.getByText('Активных процессов: 1. Автообновление включено.')).toBeInTheDocument();
  });

  it('does not crash on numeric/unknown/null enum values and malformed rows', async () => {
    getSystemProcessRunsMock.mockResolvedValue({
      data: {
        page: 1,
        pageSize: 50,
        totalCount: 4,
        serverNowUtc: '2026-08-25T09:01:00Z',
        items: [
          { ...defaultRun, id: 11, status: 1, trigger: 2 },
          { ...defaultRun, id: 12, status: 'FutureState', trigger: 'FutureTrigger' },
          { ...defaultRun, id: 13, status: null, trigger: null, processType: null, displayName: null },
          null,
        ],
      },
    });
    getSystemProcessRunsSummaryMock.mockResolvedValue({ data: { activeCount: 0 } });

    renderPage();

    expect(await screen.findByText('Журнал процессов')).toBeInTheDocument();
    expect(screen.getAllByText('1').length).toBeGreaterThan(0);
    expect(screen.getAllByText('2').length).toBeGreaterThan(0);
    expect(screen.getByText('FutureState')).toBeInTheDocument();
    expect(screen.getByText('FutureTrigger')).toBeInTheDocument();
    expect(screen.getAllByText('Неизвестно').length).toBeGreaterThan(0);
  });

  it('keeps loading/empty/error states visible instead of white screen', async () => {
    let resolveList: ((value: unknown) => void) | null = null;
    getSystemProcessRunsMock.mockImplementation(() => new Promise((resolve) => {
      resolveList = resolve;
    }));
    getSystemProcessRunsSummaryMock.mockResolvedValue({ data: { activeCount: 0 } });

    renderPage();

    expect(await screen.findByText('Загрузка...')).toBeInTheDocument();

    resolveList?.({
      data: {
        page: 1,
        pageSize: 50,
        totalCount: 0,
        serverNowUtc: '2026-08-25T09:01:00Z',
        items: [],
      },
    });
    await waitFor(() => expect(screen.getByText('Нет данных для выбранных фильтров')).toBeInTheDocument());

    getSystemProcessRunsMock.mockRejectedValueOnce(new Error('boom'));
    await waitFor(() => expect(getSystemProcessRunsMock).toHaveBeenCalledTimes(1));
    const refreshButton = screen.getByText('Обновить').closest('button');
    expect(refreshButton).not.toBeNull();
    refreshButton?.click();
    expect(await screen.findByText('Не удалось загрузить журнал процессов.')).toBeInTheDocument();
  });

  it('renders compact date/time columns and keeps status before actions', async () => {
    const completedRun = {
      ...defaultRun,
      id: 2,
      displayName: 'Ночное обновление каталога акций',
      processType: 'catalog-stock-refresh',
      trigger: 'Scheduled',
      status: 'Interrupted',
      startedAtUtc: '2026-08-25T09:00:05Z',
      completedAtUtc: '2026-08-25T09:01:24Z',
      durationSeconds: 79,
      processedItems: 12345,
      totalItems: 123456789,
      progressPercent: 10,
    };
    const runningRun = {
      ...defaultRun,
      id: 3,
      displayName: 'Выполняющийся процесс',
      completedAtUtc: null,
      status: 'Running',
    };

    getSystemProcessRunsMock.mockResolvedValue({
      data: {
        page: 1,
        pageSize: 50,
        totalCount: 120,
        serverNowUtc: '2026-08-25T09:01:00Z',
        items: [completedRun, runningRun],
      },
    });
    getSystemProcessRunsSummaryMock.mockResolvedValue({ data: { activeCount: 0 } });
    getSystemProcessRunMock.mockResolvedValue({
      data: {
        id: 2,
        displayName: completedRun.displayName,
        processType: completedRun.processType,
        trigger: completedRun.trigger,
      },
    });

    const { container } = renderPage();

    expect(await screen.findByText('Ночное обновление каталога акций')).toBeInTheDocument();
    expect(getSystemProcessRunsMock).toHaveBeenNthCalledWith(1, expect.objectContaining({ page: 1, pageSize: 50 }));

    const systemTable = container.querySelector('.ant-table-wrapper.system-process-runs-table');
    expect(systemTable).not.toBeNull();
    expect(systemTable?.querySelector('.ant-table')).toHaveClass('ant-table-small');

    const headerCells = Array.from(container.querySelectorAll('.ant-table-thead th.ant-table-cell'));
    const columnHeaders = headerCells.map((th) => th.textContent?.trim() ?? '');
    expect(columnHeaders[0]).toBe('Дата');
    expect(columnHeaders).toContain('Запуск');
    expect(columnHeaders).toContain('Заверш.');
    expect(columnHeaders).not.toContain('Завершение');
    expect(columnHeaders).toContain('Длит.');
    expect(columnHeaders).toContain('Статус');
    expect(columnHeaders).toContain('Действия');
    expect(columnHeaders.findIndex((x) => x === 'Статус')).toBeLessThan(columnHeaders.findIndex((x) => x === 'Действия'));

    const processRow = screen.getByText('Ночное обновление каталога акций').closest('tr');
    expect(processRow).not.toBeNull();
    expect(within(processRow as HTMLElement).queryByText('stock-quote-refresh-cycle')).not.toBeInTheDocument();

    expect(screen.getAllByText('25.08.2026').length).toBeGreaterThan(0);
    const startTimeCell = screen.getAllByText('09:00:05')[0];
    const completionTimeCell = screen.getAllByText('09:01:24')[0];
    expect(startTimeCell).toHaveStyle({ whiteSpace: 'nowrap' });
    expect(completionTimeCell).toHaveStyle({ whiteSpace: 'nowrap' });
    expect(screen.queryByText('25.08.2026 09:00:05')).not.toBeInTheDocument();
    expect(screen.getAllByText('1м 19с').length).toBeGreaterThan(0);

    const durationHeader = headerCells.find((th) => th.textContent?.trim() === 'Длит.');
    expect(durationHeader).toBeDefined();
    const progressHeader = headerCells.find((th) => th.textContent?.trim() === 'Прогресс');
    expect(progressHeader).toBeDefined();
    const widthCols = container.querySelectorAll('.ant-table-content table colgroup col');
    expect(widthCols[1]?.getAttribute('style') ?? '').toContain('width: 84px');
    expect(widthCols[2]?.getAttribute('style') ?? '').toContain('width: 84px');
    expect(widthCols[3]?.getAttribute('style') ?? '').toContain('width: 80px');
    expect(widthCols[6]?.getAttribute('style') ?? '').toContain('width: 140px');
    expect(container.querySelector('.ant-table-content table')?.getAttribute('style') ?? '').toContain('width: 1290px');

    const longProgress = screen.getAllByText('12345 / 123456789 (10%)')[0];
    expect(longProgress).toHaveStyle({ textOverflow: 'ellipsis', whiteSpace: 'nowrap' });

    const unfinishedRow = screen.getByText('Выполняющийся процесс').closest('tr');
    expect(unfinishedRow).not.toBeNull();
    expect(within(unfinishedRow as HTMLElement).getAllByText('—').length).toBeGreaterThan(0);

    const detailsButton = screen.getAllByRole('button', { name: 'Детали' })[0];
    detailsButton.click();
    await waitFor(() => expect(getSystemProcessRunMock).toHaveBeenCalledWith(2));
    expect(await screen.findByText('Детали процесса')).toBeInTheDocument();
    expect(screen.getByText('catalog-stock-refresh')).toBeInTheDocument();

    const page2Button = container.querySelector('.ant-pagination-item-2 a');
    expect(page2Button).not.toBeNull();
    (page2Button as HTMLElement).click();
    await waitFor(() => {
      expect(getSystemProcessRunsMock).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2, pageSize: 50 }));
    });

    const sizeSelector = container.querySelector('.ant-pagination-options .ant-select-selector');
    expect(sizeSelector).not.toBeNull();
    fireEvent.mouseDown(sizeSelector as HTMLElement);
    const size25Option = await screen.findByText('25 / page');
    size25Option.click();
    await waitFor(() => {
      expect(getSystemProcessRunsMock).toHaveBeenLastCalledWith(expect.objectContaining({ pageSize: 25 }));
    });
  });

  it('keeps compact density table-specific (does not affect unrelated default tables)', async () => {
    getSystemProcessRunsMock.mockResolvedValue({
      data: {
        page: 1,
        pageSize: 50,
        totalCount: 1,
        serverNowUtc: '2026-08-25T09:01:00Z',
        items: [defaultRun],
      },
    });
    getSystemProcessRunsSummaryMock.mockResolvedValue({ data: { activeCount: 0 } });

    const { container } = renderPage();
    expect(await screen.findByText('Обновление каталога акций')).toBeInTheDocument();
    expect(container.querySelector('.ant-table-wrapper.system-process-runs-table .ant-table')).toHaveClass('ant-table-small');

    const { container: genericContainer } = render(
      <Table
        columns={[{ title: 'Test', dataIndex: 'value', key: 'value' }]}
        dataSource={[{ key: 'r1', value: 'v1' }]}
        pagination={false}
      />,
    );
    expect(genericContainer.querySelector('.ant-table')).not.toHaveClass('ant-table-small');
  });
});
