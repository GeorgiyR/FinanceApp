import React from 'react';
import { Button, Drawer, Select, Space, Typography } from 'antd';
import type { AdvancedStockFilters } from './stockFilterModel';

const { Text } = Typography;

export const ADVANCED_EXCHANGE_OPTIONS: Array<{ value: 'fra' | 'us'; label: string }> = [
  { value: 'fra', label: 'Frankfurt (FRA)' },
  { value: 'us', label: 'США (NYSE + NASDAQ)' },
];

type Option = { value: number; label: string };

type AdvancedStockFiltersDrawerProps = {
  open: boolean;
  draftFilters: AdvancedStockFilters;
  sectorOptions: Option[];
  industryOptions: Option[];
  onClose: () => void;
  onDraftChange: (next: AdvancedStockFilters) => void;
  onClearDraft: () => void;
  onApply: () => void;
};

const AdvancedStockFiltersDrawer: React.FC<AdvancedStockFiltersDrawerProps> = ({
  open,
  draftFilters,
  sectorOptions,
  industryOptions,
  onClose,
  onDraftChange,
  onClearDraft,
  onApply,
}) => (
  <Drawer
    title="Расширенные фильтры"
    placement="right"
    width="min(420px, 100vw)"
    styles={{ body: { paddingBottom: 96 } }}
    open={open}
    onClose={onClose}
    destroyOnClose={false}
    footer={(
      <Space style={{ display: 'flex', justifyContent: 'space-between' }}>
        <Button onClick={onClearDraft}>Очистить</Button>
        <Button type="primary" onClick={onApply}>Применить</Button>
      </Space>
    )}
  >
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <label htmlFor="advanced-stock-filter-exchanges">
        <Text strong>Биржа</Text>
      </label>
      <Select
        id="advanced-stock-filter-exchanges"
        mode="multiple"
        allowClear
        placeholder="Выберите биржу"
        options={ADVANCED_EXCHANGE_OPTIONS}
        value={draftFilters.exchanges}
        onChange={(values) => onDraftChange({ ...draftFilters, exchanges: values })}
        style={{ width: '100%' }}
        aria-label="Фильтр по бирже"
      />

      <label htmlFor="advanced-stock-filter-sectors">
        <Text strong>Сектор</Text>
      </label>
      <Select
        id="advanced-stock-filter-sectors"
        mode="multiple"
        allowClear
        placeholder="Выберите сектор"
        options={sectorOptions}
        value={draftFilters.sectorIds}
        onChange={(values) => onDraftChange({ ...draftFilters, sectorIds: values })}
        style={{ width: '100%' }}
        aria-label="Фильтр по сектору"
      />

      <label htmlFor="advanced-stock-filter-industries">
        <Text strong>Отрасль</Text>
      </label>
      <Select
        id="advanced-stock-filter-industries"
        mode="multiple"
        allowClear
        placeholder="Выберите отрасль"
        options={industryOptions}
        value={draftFilters.industryIds}
        onChange={(values) => onDraftChange({ ...draftFilters, industryIds: values })}
        style={{ width: '100%' }}
        aria-label="Фильтр по отрасли"
      />
    </Space>
  </Drawer>
);

export default AdvancedStockFiltersDrawer;
