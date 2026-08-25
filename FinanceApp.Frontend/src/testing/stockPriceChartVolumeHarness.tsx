import React from 'react';
import { createRoot } from 'react-dom/client';
import StockPriceChart from '../components/StockPriceChart';
import type { StockHistoryPoint, StockHistoryRange, StockHistoryResponse } from '../types';

const fixtureFrankfurt1yVolumes = [
  60000, 58000, 55000, 52000, 50000, 47000, 45000, 43000, 41000, 39000, 37000, 35000, 33000,
  31000, 29000, 27000, 25000, 24000, 23000, 22000, 21000, 20000, 19000, 18000, 17000, 16000,
  15000, 14500, 14000, 13500, 13000, 12500, 12000, 18000, 17000, 16000, 15500, 15000, 14500,
  14000, 13500, 13000, 12500, 12000, 11500, 11000, 10500, 10000, 9500, 9000, 8500, 8000, 204,
];

const createPoint = (timestamp: Date, interval: string, volume: number): StockHistoryPoint => ({
  timestamp: timestamp.toISOString(),
  interval,
  openRaw: 100,
  highRaw: 110,
  lowRaw: 90,
  closeRaw: 100,
  openNormalized: 100,
  highNormalized: 110,
  lowNormalized: 90,
  closeNormalized: 100,
  openEur: 100,
  highEur: 110,
  lowEur: 90,
  closeEur: 100,
  volume,
  isQuoteDerived: false,
});

const buildPoints = ({
  count,
  interval,
  startDateUtc,
  stepMs,
  baseVolume,
  volumeDelta,
}: {
  count: number;
  interval: string;
  startDateUtc: Date;
  stepMs: number;
  baseVolume: number;
  volumeDelta: number;
}): StockHistoryPoint[] => Array.from({ length: count }, (_, index) =>
  createPoint(
    new Date(startDateUtc.getTime() + index * stepMs),
    interval,
    Math.max(10, baseVolume + index * volumeDelta),
  ));

const buildResponse = (
  range: StockHistoryRange,
  exchange: 'Frankfurt' | 'NYSE' | 'NASDAQ',
): StockHistoryResponse => {
  const defaultMetrics = {
    averageVolume20: 14229.95,
    averageVolume50: 24352.46,
    relativeVolume: null,
    turnover: null,
    turnoverCurrency: null,
    latestMetricsTimestamp: null,
    usesCompletedCandle: true,
  };

  if (range === '3y') {
    const points = buildPoints({
      count: 36,
      interval: '1mo',
      startDateUtc: new Date(Date.UTC(2023, 0, 1)),
      stepMs: 30 * 24 * 60 * 60 * 1000,
      baseVolume: 18000,
      volumeDelta: -130,
    });
    return {
      range,
      interval: '1mo',
      currency: 'EUR',
      financialCurrency: 'EUR',
      normalizedQuoteCurrency: 'EUR',
      quoteUnitMultiplier: 1,
      rateToEur: null,
      rateTimestampUtc: null,
      rateSource: null,
      conversionWarning: null,
      volumeMetrics: { ...defaultMetrics, latestMetricsTimestamp: points[points.length - 1]?.timestamp ?? null },
      points,
    };
  }

  if (range === '5y') {
    const points = buildPoints({
      count: 60,
      interval: '1mo',
      startDateUtc: new Date(Date.UTC(2021, 0, 1)),
      stepMs: 30 * 24 * 60 * 60 * 1000,
      baseVolume: 22000,
      volumeDelta: -140,
    });
    return {
      range,
      interval: '1mo',
      currency: 'EUR',
      financialCurrency: 'EUR',
      normalizedQuoteCurrency: 'EUR',
      quoteUnitMultiplier: 1,
      rateToEur: null,
      rateTimestampUtc: null,
      rateSource: null,
      conversionWarning: null,
      volumeMetrics: { ...defaultMetrics, latestMetricsTimestamp: points[points.length - 1]?.timestamp ?? null },
      points,
    };
  }

  if (range === 'today') {
    const points = buildPoints({
      count: 320,
      interval: '5m',
      startDateUtc: new Date(Date.UTC(2025, 0, 6, 12, 0, 0)),
      stepMs: 5 * 60 * 1000,
      baseVolume: exchange === 'Frankfurt' ? 850 : 500,
      volumeDelta: 1,
    });
    return {
      range,
      interval: '5m',
      currency: 'USD',
      financialCurrency: 'USD',
      normalizedQuoteCurrency: 'USD',
      quoteUnitMultiplier: 1,
      rateToEur: null,
      rateTimestampUtc: null,
      rateSource: null,
      conversionWarning: null,
      volumeMetrics: { ...defaultMetrics, averageVolume20: 590, averageVolume50: 565, latestMetricsTimestamp: points[points.length - 1]?.timestamp ?? null },
      points,
    };
  }

  const oneYearPoints = fixtureFrankfurt1yVolumes.map((volume, index) =>
    createPoint(new Date(Date.UTC(2025, 0, 6 + index * 7)), '1wk', volume));

  return {
    range: '1y',
    interval: '1wk',
    currency: exchange === 'Frankfurt' ? 'EUR' : 'USD',
    financialCurrency: exchange === 'Frankfurt' ? 'EUR' : 'USD',
    normalizedQuoteCurrency: exchange === 'Frankfurt' ? 'EUR' : 'USD',
    quoteUnitMultiplier: 1,
    rateToEur: null,
    rateTimestampUtc: null,
    rateSource: null,
    conversionWarning: null,
    volumeMetrics: { ...defaultMetrics, latestMetricsTimestamp: oneYearPoints[oneYearPoints.length - 1]?.timestamp ?? null },
    points: oneYearPoints,
  };
};

const query = new URLSearchParams(window.location.search);
const scenario = query.get('scenario') ?? 'frankfurt';

const exchange = scenario === 'nyse'
  ? 'NYSE'
  : scenario === 'nasdaq'
    ? 'NASDAQ'
    : 'Frankfurt';

createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <div style={{ width: 1200, margin: '0 auto', padding: 24 }}>
      <StockPriceChart
        panelId={`browser-harness-${scenario}`}
        stockId={101}
        ticker={exchange === 'Frankfurt' ? 'AMZ' : 'AMD'}
        name={exchange === 'Frankfurt' ? 'Amazon' : 'AMD'}
        exchange={exchange}
        providerSymbol={exchange === 'Frankfurt' ? 'AMZ.F' : 'AMD'}
        historyLoader={async ({ range }) => buildResponse(range, exchange)}
        hideTechnicalAnalysisPanel
      />
    </div>
  </React.StrictMode>,
);
