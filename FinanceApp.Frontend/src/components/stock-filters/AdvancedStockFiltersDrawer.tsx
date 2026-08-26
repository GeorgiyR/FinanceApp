import React from 'react';
import { CheckOutlined, CloseOutlined } from '@ant-design/icons';
import { Button, Drawer, Select, Space, Tooltip, Typography } from 'antd';
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
    title={(
      <Space size={8} align="center" style={{ display: 'inline-flex', width: '100%' }}>
        <span>Расширенные фильтры</span>
        <Tooltip title="Применить фильтры">
          <Button
            type="text"
            icon={<CheckOutlined />}
            aria-label="Применить фильтры"
            onClick={onApply}
          />
        </Tooltip>
        <Tooltip title="Очистить фильтры">
          <Button
            type="text"
            danger
            icon={<CloseOutlined />}
            aria-label="Очистить фильтры"
            onClick={onClearDraft}
          />
        </Tooltip>
      </Space>
    )}
    placement="right"
    width="min(420px, 100vw)"
    open={open}
    onClose={onClose}
    destroyOnClose={false}
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
