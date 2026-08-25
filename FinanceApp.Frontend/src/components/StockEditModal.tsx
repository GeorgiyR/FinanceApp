import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Form, Input, InputNumber, Modal, Select, Spin, Tag, Typography } from 'antd';
import type { NamePath } from 'antd/es/form/interface';
import type { RuleObject } from 'antd/es/form';
import type { StoreValue } from 'antd/es/form/interface';
import { getMarketIndices, getSectors } from '../services/api';
import type {
  CreateStockRequest,
  MarketIndex,
  SectorDto,
  Stock,
  StockDependencyBlockerResponse,
  StockExchange,
  UpdateStockMetadataRequest,
} from '../types';
import { isValidFinanzenNetSlug } from '../utils/finanzenNet';

export const DEFAULT_STOCK_EXCHANGE: StockExchange = 'NYSE';
const exchangeLabelByValue: Record<StockExchange, string> = {
  NYSE: 'NYSE',
  NASDAQ: 'NASDAQ',
  Frankfurt: 'Frankfurt',
};
export const exchangeOptions: { label: string; value: StockExchange }[] = [
  { label: exchangeLabelByValue.NYSE, value: 'NYSE' },
  { label: exchangeLabelByValue.NASDAQ, value: 'NASDAQ' },
  { label: exchangeLabelByValue.Frankfurt, value: 'Frankfurt' },
];
export const IDENTITY_IMMUTABLE_HELPER =
  'Тикер и биржа определяют инструмент и не могут быть изменены. Для другого тикера или биржи создайте новую акцию.';
const IDENTITY_EDIT_WARNING =
  'Изменение тикера/биржи меняет инструмент. История котировок, provider-данные и снапшоты будут очищены и загружены заново.';
export const STOCK_MARKET_INDEX_SELECT_MODE = 'multiple';

export type StockFormValues = {
  ticker: string;
  name: string;
  commonName?: string;
  exchange: StockExchange;
  currentPrice: number;
  wkn?: string;
  isin?: string;
  finanzenNetSlug?: string;
  sectorId?: number;
  industryId?: number | null;
  marketIndexIds?: number[];
};

type ClassificationOption = {
  value: number;
  label: React.ReactNode;
};

export type StockMetadataLookups = {
  sectors: SectorDto[];
  marketIndices: MarketIndex[];
  marketIndicesLoadFailed: boolean;
};

const normalizeId = (value?: string): string | null => {
  const normalized = (value ?? '').trim().toUpperCase();
  return normalized.length > 0 ? normalized : null;
};

const renderClassificationName = (name: string, isArchived: boolean) => (
  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
    <span>{name}</span>
    {isArchived && (
      <Tag color="default" style={{ marginInlineEnd: 0 }}>
        Архив
      </Tag>
    )}
  </span>
);

export const validateStockWkn = (value?: string): true | string => {
  const normalized = (value ?? '').trim().toUpperCase();
  if (normalized.length === 0 || /^[A-Z0-9]{6}$/.test(normalized)) {
    return true;
  }

  return 'WKN: ровно 6 буквенно-цифровых символов';
};

export const validateStockIsin = (value?: string): true | string => {
  const normalized = (value ?? '').trim().toUpperCase();
  if (normalized.length === 0 || /^[A-Z]{2}[A-Z0-9]{10}$/.test(normalized)) {
    return true;
  }

  return 'ISIN: 2 буквы страны + 10 буквенно-цифровых символов';
};

export const validateStockFinanzenNetSlug = (value?: string): true | string => {
  const normalized = (value ?? '').trim().toLowerCase();
  if (normalized.length === 0 || isValidFinanzenNetSlug(normalized)) {
    return true;
  }

  return 'Разрешены строчные буквы, цифры, дефисы и подчёркивания; первый символ — буква или цифра';
};

const buildValidator =
  (validate: (value?: string) => true | string) =>
  async (_: RuleObject, value: StoreValue) => {
    const result = validate(typeof value === 'string' ? value : undefined);
    if (result === true) {
      return;
    }

    throw new Error(result);
  };

export const buildCreateStockPayload = (values: StockFormValues): CreateStockRequest => {
  const normalizedName = values.name.trim();
  const normalizedCommonName = (values.commonName ?? '').trim() || normalizedName;

  return {
    ...values,
    name: normalizedName,
    commonName: normalizedCommonName,
    wkn: normalizeId(values.wkn),
    isin: normalizeId(values.isin),
    finanzenNetSlug: (values.finanzenNetSlug ?? '').trim().toLowerCase() || null,
    exchange: values.exchange,
    sectorId: values.sectorId ?? null,
    industryId: values.industryId ?? null,
    marketIndexIds: values.marketIndexIds ?? [],
  };
};

export const buildUpdateStockMetadataPayload = (values: StockFormValues): UpdateStockMetadataRequest => {
  const payload = buildCreateStockPayload(values);
  return {
    name: payload.name,
    commonName: payload.commonName,
    wkn: payload.wkn,
    isin: payload.isin,
    finanzenNetSlug: payload.finanzenNetSlug,
    currentPrice: payload.currentPrice,
    sectorId: payload.sectorId,
    industryId: payload.industryId,
    marketIndexIds: payload.marketIndexIds,
  };
};

export const buildStockFormValues = (stock: Stock | null | undefined): StockFormValues => ({
  ticker: stock?.ticker ?? '',
  name: stock?.name ?? '',
  commonName: stock?.commonName ?? '',
  exchange: stock?.exchange ?? DEFAULT_STOCK_EXCHANGE,
  currentPrice: stock?.currentPrice ?? 0,
  wkn: stock?.wkn ?? undefined,
  isin: stock?.isin ?? undefined,
  finanzenNetSlug: stock?.finanzenNetSlug ?? undefined,
  sectorId: stock?.sector?.id,
  industryId: stock?.industryId ?? undefined,
  marketIndexIds: stock?.marketIndexIds ?? [],
});

export const buildSectorOptions = (
  sectors: SectorDto[],
  stock: Stock | null | undefined,
): ClassificationOption[] => {
  const options = sectors
    .filter((sector) => !sector.isArchived)
    .map((sector) => ({
      value: sector.id,
      label: renderClassificationName(sector.name, sector.isArchived),
    }));

  if (
    stock?.sector
    && stock.sector.isArchived
    && !options.some((option) => option.value === stock.sector?.id)
  ) {
    options.push({
      value: stock.sector.id,
      label: renderClassificationName(stock.sector.name, true),
    });
  }

  return options;
};

export const buildIndustryOptions = ({
  sectors,
  stock,
  selectedSectorId,
}: {
  sectors: SectorDto[];
  stock: Stock | null | undefined;
  selectedSectorId?: number;
}): ClassificationOption[] => {
  if (selectedSectorId == null) {
    return [];
  }

  const selectedSector = sectors.find((sector) => sector.id === selectedSectorId);
  const options = (selectedSector?.industries ?? [])
    .filter((industry) => !industry.isArchived)
    .map((industry) => ({
      value: industry.id,
      label: renderClassificationName(industry.name, industry.isArchived),
    }));

  if (
    stock?.industry
    && stock.industry.isArchived
    && stock.sector?.id === selectedSectorId
    && !options.some((option) => option.value === stock.industry?.id)
  ) {
    options.push({
      value: stock.industry.id,
      label: renderClassificationName(stock.industry.name, true),
    });
  }

  return options;
};

export const buildMarketIndexOptions = ({
  marketIndices,
  selectedMarketIndexIds,
}: {
  marketIndices: MarketIndex[];
  selectedMarketIndexIds: number[];
}): ClassificationOption[] => {
  const options = marketIndices
    .filter((marketIndex) => !marketIndex.isArchived)
    .map((marketIndex) => ({
      value: marketIndex.id,
      label: renderClassificationName(`${marketIndex.code} — ${marketIndex.name}`, marketIndex.isArchived),
    }));

  selectedMarketIndexIds.forEach((marketIndexId) => {
    const marketIndex = marketIndices.find((item) => item.id === marketIndexId);
    if (marketIndex && marketIndex.isArchived && !options.some((option) => option.value === marketIndex.id)) {
      options.push({
        value: marketIndex.id,
        label: renderClassificationName(`${marketIndex.code} — ${marketIndex.name}`, true),
      });
    }
  });

  return options;
};

export const loadStockMetadataLookups = async (): Promise<StockMetadataLookups> => {
  const [sectors, marketIndicesResponse] = await Promise.all([
    getSectors(true),
    getMarketIndices(true).catch(() => null),
  ]);

  return {
    sectors,
    marketIndices: marketIndicesResponse ?? [],
    marketIndicesLoadFailed: marketIndicesResponse == null,
  };
};

type StockEditModalProps = {
  open: boolean;
  mode: 'create' | 'edit';
  stock: Stock | null;
  sectors: SectorDto[];
  marketIndices: MarketIndex[];
  loading?: boolean;
  submitting?: boolean;
  allowIdentityEditing?: boolean;
  inlineError?: string | null;
  inlineBlockers?: StockDependencyBlockerResponse[];
  onCancel: () => void;
  onSubmit: (values: StockFormValues, context: { identityEditingEnabled: boolean }) => void | Promise<void>;
  onPermanentDelete?: () => void;
};

const StockEditModal: React.FC<StockEditModalProps> = ({
  open,
  mode,
  stock,
  sectors,
  marketIndices,
  loading = false,
  submitting = false,
  allowIdentityEditing = true,
  inlineError = null,
  inlineBlockers = [],
  onCancel,
  onSubmit,
  onPermanentDelete,
}) => {
  const [form] = Form.useForm<StockFormValues>();
  const [identityEditingEnabled, setIdentityEditingEnabled] = useState(false);
  const [validationSummary, setValidationSummary] = useState<string | null>(null);
  const initializedFormKeyRef = useRef<string | null>(null);
  const selectedFormSectorId = Form.useWatch('sectorId', form) as number | undefined;

  useEffect(() => {
    if (!open) {
      form.resetFields();
      setIdentityEditingEnabled(false);
      setValidationSummary(null);
      initializedFormKeyRef.current = null;
      return;
    }

    const formKey = mode === 'edit' ? `edit:${stock?.id ?? 'none'}` : 'create';
    if (initializedFormKeyRef.current === formKey) {
      return;
    }

    form.setFieldsValue(buildStockFormValues(mode === 'edit' ? stock : null));
    setIdentityEditingEnabled(false);
    setValidationSummary(null);
    initializedFormKeyRef.current = formKey;
  }, [form, mode, open, stock?.id]);

  const sectorOptions = useMemo(
    () => buildSectorOptions(sectors, mode === 'edit' ? stock : null),
    [mode, sectors, stock],
  );
  const industryOptions = useMemo(
    () => buildIndustryOptions({
      sectors,
      stock: mode === 'edit' ? stock : null,
      selectedSectorId: selectedFormSectorId,
    }),
    [mode, sectors, selectedFormSectorId, stock],
  );
  const marketIndexOptions = useMemo(
    () => buildMarketIndexOptions({
      marketIndices,
      selectedMarketIndexIds: mode === 'edit' ? stock?.marketIndexIds ?? [] : [],
    }),
    [marketIndices, mode, stock?.marketIndexIds],
  );

  const handleCancel = () => {
    form.resetFields();
    setValidationSummary(null);
    initializedFormKeyRef.current = null;
    onCancel();
  };

  const focusErrorField = (namePath: NamePath) => {
    form.scrollToField(namePath, { block: 'center' });
    requestAnimationFrame(() => {
      const firstInput = document.querySelector<HTMLElement>(
        '.ant-form-item-has-error input, .ant-form-item-has-error textarea, .ant-form-item-has-error .ant-select-selector',
      );
      firstInput?.focus();
    });
  };

  return (
    <Modal
      title={mode === 'edit' ? 'Редактировать акцию' : 'Добавить акцию'}
      open={open}
      onCancel={handleCancel}
      footer={null}
      destroyOnHidden
    >
      {loading ? (
        <div style={{ display: 'flex', justifyContent: 'center', padding: 48 }}>
          <Spin />
        </div>
      ) : (
        <Form
          form={form}
          layout="vertical"
          initialValues={{ exchange: DEFAULT_STOCK_EXCHANGE }}
          scrollToFirstError={{ block: 'center' }}
          onValuesChange={() => {
            if (validationSummary) {
              setValidationSummary(null);
            }
          }}
          onFinish={async (values) => {
            setValidationSummary(null);
            await onSubmit(values, { identityEditingEnabled });
          }}
          onFinishFailed={(errorInfo) => {
            const firstError = errorInfo.errorFields[0];
            if (firstError) {
              focusErrorField(firstError.name);
            }
            setValidationSummary('Проверьте обязательные поля и исправьте ошибки формы.');
          }}
        >
          {inlineError && (
            <Form.Item>
              <Alert
                type="error"
                showIcon
                message={inlineError}
                description={inlineBlockers.length > 0 ? (
                  <ul style={{ margin: 0, paddingInlineStart: 18 }}>
                    {inlineBlockers.map((blocker) => (
                      <li key={`${blocker.category}-${blocker.displayName}`}>
                        {`${blocker.displayName}: ${blocker.count}`}
                        {blocker.relatedNames.length > 0 ? ` (${blocker.relatedNames.join(', ')})` : ''}
                      </li>
                    ))}
                  </ul>
                ) : undefined}
              />
            </Form.Item>
          )}
          <Form.Item
            label="Тикер"
            name="ticker"
            rules={[{ required: true, message: 'Введите тикер' }]}
            extra={mode === 'edit' ? IDENTITY_IMMUTABLE_HELPER : undefined}
          >
            <Input placeholder="AAPL" disabled={mode === 'edit' && (!allowIdentityEditing || !identityEditingEnabled)} />
          </Form.Item>
          <Form.Item
            label="Название"
            name="name"
            rules={[{ required: true, message: 'Введите название' }]}
          >
            <Input placeholder="Apple Inc." />
          </Form.Item>
          <Form.Item
            label="Общее название"
            name="commonName"
            extra="Используется для обозначения одной и той же компании/бумаги на разных биржах."
          >
            <Input placeholder="Если оставить пустым, будет использовано поле «Название»" />
          </Form.Item>
          <Form.Item
            label="Биржа"
            name="exchange"
            rules={[{ required: true, message: 'Выберите биржу' }]}
          >
            <Select options={exchangeOptions} disabled={mode === 'edit' && (!allowIdentityEditing || !identityEditingEnabled)} />
          </Form.Item>
          {mode === 'edit' && allowIdentityEditing && (
            <Form.Item>
              {!identityEditingEnabled ? (
                <Button
                  block
                  htmlType="button"
                  onClick={() => {
                    Modal.confirm({
                      title: 'Изменить тикер / биржу',
                      content: (
                        <Alert
                          type="warning"
                          showIcon
                          message={IDENTITY_EDIT_WARNING}
                        />
                      ),
                      okText: 'Подтвердить',
                      cancelText: 'Отмена',
                      onOk: () => setIdentityEditingEnabled(true),
                    });
                  }}
                >
                  Изменить тикер / биржу
                </Button>
              ) : (
                <Alert
                  type="warning"
                  showIcon
                  message="Режим коррекции идентичности включён"
                  description={IDENTITY_EDIT_WARNING}
                />
              )}
            </Form.Item>
          )}
          {mode === 'edit' && !allowIdentityEditing && (
            <Form.Item>
              <Alert
                type="info"
                showIcon
                message="Изменение тикера/биржи недоступно в этом режиме. Разрешено только редактирование метаданных."
              />
            </Form.Item>
          )}
          <Form.Item label="Сектор" name="sectorId">
            <Select
              allowClear
              placeholder="Не выбран"
              options={sectorOptions}
              onChange={(value) => {
                form.setFieldsValue({ sectorId: value, industryId: undefined });
              }}
            />
          </Form.Item>
          <Form.Item label="Отрасль" name="industryId">
            <Select
              allowClear
              placeholder={selectedFormSectorId != null ? 'Не выбрана' : 'Сначала выберите сектор'}
              options={industryOptions}
              disabled={selectedFormSectorId == null}
            />
          </Form.Item>
          <Form.Item label="Мировые индексы" name="marketIndexIds">
            <Select
              mode={STOCK_MARKET_INDEX_SELECT_MODE}
              allowClear
              placeholder="Не выбраны"
              options={marketIndexOptions}
            />
          </Form.Item>
          <Form.Item
            label="Текущая цена (€)"
            name="currentPrice"
            rules={[{ required: true, message: 'Введите текущую цену' }]}
          >
            <InputNumber
              min={0}
              step={0.01}
              style={{ width: '100%' }}
              placeholder="0.00"
              prefix="€"
            />
          </Form.Item>
          <Form.Item
            label="WKN"
            name="wkn"
            rules={[{ validator: buildValidator(validateStockWkn) }]}
          >
            <Input
              placeholder="865985"
              maxLength={6}
              onChange={(e) => {
                form.setFieldValue('wkn', e.target.value.toUpperCase());
              }}
            />
          </Form.Item>
          <Form.Item
            label="ISIN"
            name="isin"
            rules={[{ validator: buildValidator(validateStockIsin) }]}
          >
            <Input
              placeholder="US0378331005"
              maxLength={12}
              onChange={(e) => {
                form.setFieldValue('isin', e.target.value.toUpperCase());
              }}
            />
          </Form.Item>
          <Form.Item
            label="finanzen.net Slug"
            name="finanzenNetSlug"
            tooltip="Часть URL после /aktien/. Например: western_digital-aktie. Разрешены строчные буквы, цифры, дефисы и подчёркивания. Оставьте пустым, если не нужно."
            rules={[{ validator: buildValidator(validateStockFinanzenNetSlug) }]}
          >
            <Input
              placeholder="western_digital-aktie"
              maxLength={120}
              onChange={(e) => {
                form.setFieldValue('finanzenNetSlug', e.target.value.toLowerCase());
              }}
            />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={submitting} block>
              {mode === 'edit' ? 'Сохранить' : 'Добавить'}
            </Button>
          </Form.Item>
          {(validationSummary || inlineError) && (
            <Form.Item>
              <Alert
                type="error"
                showIcon
                message={validationSummary ?? inlineError}
              />
            </Form.Item>
          )}
          {mode === 'edit' && onPermanentDelete && (
            <Form.Item style={{ marginBottom: 0 }}>
              <Typography.Text type="secondary" style={{ display: 'block', marginBottom: 8 }}>
                Необратимо удаляет акцию и технические данные. Бизнес-ссылки блокируют удаление.
              </Typography.Text>
              <Button danger block htmlType="button" onClick={onPermanentDelete}>
                Удалить акцию полностью
              </Button>
            </Form.Item>
          )}
        </Form>
      )}
    </Modal>
  );
};

export default StockEditModal;
