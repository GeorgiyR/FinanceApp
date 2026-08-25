import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Alert,
  Button,
  DatePicker,
  Drawer,
  Input,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import dayjs, { Dayjs } from 'dayjs';
import { ReloadOutlined } from '@ant-design/icons';
import AuthenticatedShell from '../components/AuthenticatedShell';
import { SYSTEM_PROCESS_JOURNAL_KEY } from '../components/AppSidebar';
import { useAuth } from '../contexts/AuthContext';
import {
  getPortfolios,
  getSystemProcessRun,
  getSystemProcessRuns,
  getSystemProcessRunsSummary,
} from '../services/api';
import type {
  Portfolio,
  SystemProcessRunDetails,
  SystemProcessRunListItem,
  SystemProcessRunStatus,
  SystemProcessTrigger,
} from '../types';

const { Title, Text } = Typography;
const { RangePicker } = DatePicker;

type Filters = {
  page: number;
  pageSize: number;
  statuses: SystemProcessRunStatus[];
  processType?: string;
  trigger?: SystemProcessTrigger;
  search?: string;
  fromUtc?: string;
  toUtc?: string;
  activeOnly: boolean;
};

const STATUS_META: Record<SystemProcessRunStatus, { label: string; color: string }> = {
  Pending: { label: 'Ожидает', color: 'default' },
  Running: { label: 'Выполняется', color: 'processing' },
  Succeeded: { label: 'Успешно', color: 'success' },
  CompletedWithErrors: { label: 'Завершено с ошибками', color: 'warning' },
  Failed: { label: 'Ошибка', color: 'error' },
  Cancelled: { label: 'Отменено', color: 'default' },
  Interrupted: { label: 'Прервано', color: 'volcano' },
  Deferred: { label: 'Отложено', color: 'gold' },
};

const TRIGGER_LABELS: Record<SystemProcessTrigger, string> = {
  Scheduled: 'По расписанию',
  StartupCatchUp: 'Startup catch-up',
  Automatic: 'Автоматически',
  Manual: 'Вручную',
  ApiRepair: 'API/восстановление',
  SystemRecovery: 'Системное восстановление',
};

const STATUS_OPTIONS = Object.keys(STATUS_META) as SystemProcessRunStatus[];
const TRIGGER_OPTIONS = Object.keys(TRIGGER_LABELS) as SystemProcessTrigger[];
const PAGE_SIZE_OPTIONS = ['10', '25', '50', '100'];

const formatDate = (value?: string | null): string => (value ? dayjs(value).format('DD.MM.YYYY HH:mm:ss') : '—');

const formatDuration = (seconds?: number | null): string => {
  if (seconds == null || Number.isNaN(seconds) || seconds < 0) {
    return '—';
  }

  const total = Math.floor(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) {
    return `${h}ч ${m}м ${s}с`;
  }
  if (m > 0) {
    return `${m}м ${s}с`;
  }
  return `${s}с`;
};

const SystemProcessRunsPage: React.FC = () => {
  const { user, logout } = useAuth();
  const [messageApi, contextHolder] = message.useMessage();
  const [portfolios, setPortfolios] = useState<Portfolio[]>([]);
  const [runs, setRuns] = useState<SystemProcessRunListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [totalCount, setTotalCount] = useState(0);
  const [serverNowUtc, setServerNowUtc] = useState<string | null>(null);
  const [summaryActiveCount, setSummaryActiveCount] = useState(0);
  const [filters, setFilters] = useState<Filters>({ page: 1, pageSize: 25, statuses: [], activeOnly: false });
  const [dateRange, setDateRange] = useState<[Dayjs | null, Dayjs | null]>([null, null]);
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [selectedDetails, setSelectedDetails] = useState<SystemProcessRunDetails | null>(null);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [tick, setTick] = useState(Date.now());
  const requestIdRef = useRef(0);
  const loadingRef = useRef(false);

  useEffect(() => {
    let cancelled = false;
    getPortfolios().then((res) => {
      if (!cancelled) {
        setPortfolios(res.data);
      }
    }).catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);

  const load = useCallback(async () => {
    if (loadingRef.current) return;
    loadingRef.current = true;
    const requestId = ++requestIdRef.current;
    setLoading(true);
    setError(null);

    try {
      const params: Record<string, unknown> = {
        page: filters.page,
        pageSize: filters.pageSize,
        activeOnly: filters.activeOnly,
      };
      if (filters.statuses.length > 0) params.statuses = filters.statuses.join(',');
      if (filters.processType) params.processType = filters.processType;
      if (filters.trigger) params.trigger = filters.trigger;
      if (filters.search) params.search = filters.search;
      if (filters.fromUtc) params.fromUtc = filters.fromUtc;
      if (filters.toUtc) params.toUtc = filters.toUtc;

      const [listRes, summaryRes] = await Promise.all([
        getSystemProcessRuns(params),
        getSystemProcessRunsSummary(),
      ]);

      if (requestId !== requestIdRef.current) return;
      setRuns(listRes.data.items ?? []);
      setTotalCount(listRes.data.totalCount ?? 0);
      setServerNowUtc(listRes.data.serverNowUtc ?? null);
      setSummaryActiveCount(summaryRes.data.activeCount ?? 0);
    } catch {
      if (requestId !== requestIdRef.current) return;
      setError('Не удалось загрузить журнал процессов.');
    } finally {
      if (requestId === requestIdRef.current) {
        setLoading(false);
      }
      loadingRef.current = false;
    }
  }, [filters]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (summaryActiveCount <= 0) {
      return undefined;
    }

    const interval = window.setInterval(() => {
      if (document.hidden) {
        return;
      }

      void load();
    }, 7000);

    return () => {
      window.clearInterval(interval);
    };
  }, [summaryActiveCount, load]);

  useEffect(() => {
    const interval = window.setInterval(() => setTick(Date.now()), 1000);
    return () => window.clearInterval(interval);
  }, []);

  const handleRefresh = () => {
    void load();
  };

  const handleOpenDetails = async (id: number) => {
    setDetailsOpen(true);
    setDetailsLoading(true);
    setSelectedDetails(null);

    try {
      const response = await getSystemProcessRun(id);
      setSelectedDetails(response.data);
    } catch {
      messageApi.error('Не удалось загрузить детали процесса.');
    } finally {
      setDetailsLoading(false);
    }
  };

  const processTypeOptions = useMemo(() => {
    const unique = Array.from(new Set(runs.map((x) => x.processType))).filter(Boolean);
    return unique.map((value) => ({ label: value, value }));
  }, [runs]);

  const columns: ColumnsType<SystemProcessRunListItem> = [
    {
      title: 'Запуск',
      key: 'startedAtUtc',
      width: 190,
      render: (_, row) => (
        <Tooltip title={row.startedAtUtc ?? row.queuedAtUtc}>
          <span>{formatDate(row.startedAtUtc ?? row.queuedAtUtc)}</span>
        </Tooltip>
      ),
    },
    {
      title: 'Завершение',
      dataIndex: 'completedAtUtc',
      key: 'completedAtUtc',
      width: 190,
      render: (value: string | null) => <Tooltip title={value ?? ''}><span>{formatDate(value)}</span></Tooltip>,
    },
    {
      title: 'Длительность',
      key: 'durationSeconds',
      width: 160,
      render: (_, row) => {
        const isActive = row.completedAtUtc == null;
        const base = row.durationSeconds ?? null;
        if (!isActive || base == null || !serverNowUtc) {
          return formatDuration(base);
        }

        const driftSeconds = (Date.now() - dayjs(serverNowUtc).valueOf()) / 1000;
        return formatDuration(base + Math.max(0, driftSeconds));
      },
    },
    {
      title: 'Процесс',
      dataIndex: 'displayName',
      key: 'displayName',
      width: 260,
      ellipsis: true,
      render: (value: string, row) => (
        <Space direction="vertical" size={0}>
          <Text strong>{value}</Text>
          <Text type="secondary" style={{ fontSize: 12 }}>{row.processType}</Text>
        </Space>
      ),
    },
    {
      title: 'Источник',
      dataIndex: 'trigger',
      key: 'trigger',
      width: 170,
      render: (value: SystemProcessTrigger) => TRIGGER_LABELS[value] ?? value,
    },
    {
      title: 'Прогресс',
      key: 'progress',
      width: 210,
      render: (_, row) => {
        const total = row.totalItems;
        if (total && total > 0) {
          return `${row.processedItems} / ${total} (${Math.round(row.progressPercent ?? 0)}%)`;
        }

        return row.processedItems > 0 ? `${row.processedItems}` : '—';
      },
    },
    {
      title: 'Результат',
      key: 'result',
      width: 320,
      ellipsis: true,
      render: (_, row) => row.resultSummary ?? `Обработано ${row.processedItems}; успешно ${row.succeededItems}; ошибок ${row.failedItems}`,
    },
    {
      title: 'Статус',
      dataIndex: 'status',
      key: 'status',
      width: 180,
      render: (value: SystemProcessRunStatus) => {
        const meta = STATUS_META[value];
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: 'Действия',
      key: 'actions',
      width: 120,
      fixed: 'right',
      render: (_, row) => <Button size="small" onClick={() => { void handleOpenDetails(row.id); }}>Детали</Button>,
    },
  ];

  return (
    <AuthenticatedShell
      portfolios={portfolios}
      selectedKeys={[SYSTEM_PROCESS_JOURNAL_KEY]}
      onLogout={logout}
      userName={user?.username}
      headerLeft={<Title level={4} style={{ margin: 0 }}>Журнал процессов</Title>}
      headerRight={<Button icon={<ReloadOutlined />} onClick={handleRefresh}>Обновить</Button>}
    >
      {contextHolder}
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        {summaryActiveCount > 0 && (
          <Alert
            type="info"
            showIcon
            message={`Активных процессов: ${summaryActiveCount}. Автообновление включено.`}
          />
        )}

        <Space wrap>
          <Select<SystemProcessRunStatus[]>
            mode="multiple"
            allowClear
            style={{ minWidth: 220 }}
            placeholder="Статусы"
            value={filters.statuses}
            onChange={(statuses) => setFilters((prev) => ({ ...prev, page: 1, statuses }))}
            options={STATUS_OPTIONS.map((status) => ({ value: status, label: STATUS_META[status].label }))}
          />
          <Select<string>
            allowClear
            style={{ minWidth: 220 }}
            placeholder="Процесс"
            value={filters.processType}
            onChange={(processType) => setFilters((prev) => ({ ...prev, page: 1, processType: processType || undefined }))}
            options={processTypeOptions}
          />
          <Select<SystemProcessTrigger>
            allowClear
            style={{ minWidth: 220 }}
            placeholder="Источник"
            value={filters.trigger}
            onChange={(trigger) => setFilters((prev) => ({ ...prev, page: 1, trigger: trigger || undefined }))}
            options={TRIGGER_OPTIONS.map((trigger) => ({ value: trigger, label: TRIGGER_LABELS[trigger] }))}
          />
          <RangePicker
            showTime
            value={dateRange}
            onChange={(value) => {
              const next: [Dayjs | null, Dayjs | null] = value ? [value[0], value[1]] : [null, null];
              setDateRange(next);
              setFilters((prev) => ({
                ...prev,
                page: 1,
                fromUtc: next[0]?.toISOString(),
                toUtc: next[1]?.toISOString(),
              }));
            }}
          />
          <Input.Search
            allowClear
            placeholder="Поиск"
            style={{ width: 260 }}
            onSearch={(value) => setFilters((prev) => ({ ...prev, page: 1, search: value.trim() || undefined }))}
          />
          <Space>
            <Switch
              checked={filters.activeOnly}
              onChange={(activeOnly) => setFilters((prev) => ({ ...prev, page: 1, activeOnly }))}
            />
            <Text>Только активные</Text>
          </Space>
        </Space>

        {error && <Alert type="error" showIcon message={error} />}

        <Table<SystemProcessRunListItem>
          rowKey="id"
          loading={loading}
          columns={columns}
          dataSource={runs}
          scroll={{ x: 1800 }}
          pagination={{
            current: filters.page,
            pageSize: filters.pageSize,
            total: totalCount,
            showSizeChanger: true,
            pageSizeOptions: PAGE_SIZE_OPTIONS,
            onChange: (page, pageSize) => {
              setFilters((prev) => ({ ...prev, page, pageSize }));
            },
          }}
          locale={{
            emptyText: loading ? 'Загрузка...' : 'Нет данных для выбранных фильтров',
          }}
        />
      </Space>

      <Drawer
        open={detailsOpen}
        onClose={() => setDetailsOpen(false)}
        width={560}
        title="Детали процесса"
      >
        {detailsLoading && <Text>Загрузка...</Text>}
        {!detailsLoading && !selectedDetails && <Text type="secondary">Данные недоступны.</Text>}
        {!detailsLoading && selectedDetails && (
          <Space direction="vertical" size={10} style={{ width: '100%' }}>
            <Text><strong>ID:</strong> {selectedDetails.id}</Text>
            <Text><strong>Correlation ID:</strong> {selectedDetails.correlationId ?? '—'}</Text>
            <Text><strong>Процесс:</strong> {selectedDetails.displayName}</Text>
            <Text><strong>Тип:</strong> {selectedDetails.processType}</Text>
            <Text><strong>Источник:</strong> {TRIGGER_LABELS[selectedDetails.trigger] ?? selectedDetails.trigger}</Text>
            <Text><strong>Инициатор:</strong> {selectedDetails.initiatedByUserId ?? '—'}</Text>
            <Text><strong>Постановка:</strong> {formatDate(selectedDetails.queuedAtUtc)}</Text>
            <Text><strong>Запуск:</strong> {formatDate(selectedDetails.startedAtUtc)}</Text>
            <Text><strong>Завершение:</strong> {formatDate(selectedDetails.completedAtUtc)}</Text>
            <Text><strong>Обновление:</strong> {formatDate(selectedDetails.updatedAtUtc)}</Text>
            <Text><strong>Длительность:</strong> {formatDuration(selectedDetails.durationSeconds)}</Text>
            <Text><strong>Прогресс:</strong> {selectedDetails.processedItems} / {selectedDetails.totalItems ?? '—'}</Text>
            <Text><strong>Результат:</strong> {selectedDetails.resultSummary ?? '—'}</Text>
            <Text><strong>Ошибка:</strong> {selectedDetails.errorSummary ?? '—'}</Text>
            <Text><strong>Последняя сущность:</strong> {selectedDetails.lastProcessedEntity ?? '—'}</Text>
            <Text><strong>Детали JSON:</strong></Text>
            <pre style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word', maxHeight: 260, overflow: 'auto' }}>
              {selectedDetails.detailsJson ?? '—'}
            </pre>
          </Space>
        )}
      </Drawer>

      {/* force render updates for active durations */}
      <span style={{ display: 'none' }}>{tick}</span>
    </AuthenticatedShell>
  );
};

export default SystemProcessRunsPage;
