import React from 'react';
import { Button, Space } from 'antd';

type AdvancedStockFilterToolbarControlsProps = {
  activeGroupCount: number;
  onOpen: () => void;
  onReset: () => void;
  resetDisabled: boolean;
  compact?: boolean;
};

const AdvancedStockFilterToolbarControls: React.FC<AdvancedStockFilterToolbarControlsProps> = ({
  activeGroupCount,
  onOpen,
  onReset,
  resetDisabled,
  compact = false,
}) => {
  const filtersLabel = activeGroupCount > 0 ? `Фильтры (${activeGroupCount})` : 'Фильтры';

  return (
    <Space size={compact ? 6 : 8}>
      <Button size={compact ? 'small' : 'middle'} onClick={onOpen} aria-label="Открыть расширенные фильтры">
        {filtersLabel}
      </Button>
      <Button size={compact ? 'small' : 'middle'} onClick={onReset} disabled={resetDisabled} aria-label="Сбросить расширенные фильтры">
        Сбросить
      </Button>
    </Space>
  );
};

export default AdvancedStockFilterToolbarControls;
