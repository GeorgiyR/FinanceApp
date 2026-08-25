import { describe, expect, it } from 'vitest';
import { buildHistoryChartData } from './stockPriceChartData';
import {
  analyzeAdaptiveVolumeScale,
  formatVolumeTooltipValue,
  getVolumeCadenceHint,
  toDisplayVolume,
} from './stockVolumeScale';

describe('stockVolumeScale', () => {
  it('activates adaptive mode for Frankfurt with a single dominant outlier', () => {
    const analysis = analyzeAdaptiveVolumeScale('Frankfurt', [120, 160, 200, 300, 42000]);
    expect(analysis.adaptiveScaleActive).toBe(true);
    expect(analysis.hasPositiveFiniteVolume).toBe(true);
    expect(analysis.actualUpperBound).toBe(42000);
    expect(analysis.displayUpperBound).toBeLessThan(analysis.actualUpperBound ?? 0);
    expect(analysis.activationReason).toBe('maxToMedian');
  });

  it('activates for Frankfurt upper-tail cluster where max/p95<2 but p95/median is extreme', () => {
    const volumes = [100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200, 210, 220, 230, 240, 250, 260, 30000, 45000, 60000];
    const analysis = analyzeAdaptiveVolumeScale('frankfurt', volumes);
    expect(analysis.adaptiveScaleActive).toBe(true);
    expect(analysis.activationReason).toBe('combined');
    expect((analysis.actualUpperBound ?? 0) / (analysis.p95 ?? 1)).toBeLessThan(2);
  });

  it('activates on AMD-like long-range Frankfurt shape and keeps a materially lower display upper bound', () => {
    const amdLikeFrankfurt = [281, 320, 410, 520, 610, 700, 780, 860, 940, 1010, 1090, 1180, 1270, 1350, 1440, 1530, 1610, 1700, 1820, 1950, 2200, 2600, 3200, 3800, 4700, 6200, 8400, 12000, 30000, 52000, 78000];
    const analysis = analyzeAdaptiveVolumeScale('Frankfurt', amdLikeFrankfurt);
    expect(analysis.adaptiveScaleActive).toBe(true);
    expect(analysis.displayUpperBound).toBeGreaterThan(6000);
    expect(analysis.displayUpperBound).toBeLessThan(10000);
  });

  it('keeps ordinary scaling for Frankfurt when distribution is not materially skewed', () => {
    const analysis = analyzeAdaptiveVolumeScale('Frankfurt', [120, 150, 170, 200, 230, 250, 270]);
    expect(analysis.adaptiveScaleActive).toBe(false);
    expect(analysis.displayUpperBound).toBe(270);
  });

  it('keeps existing behavior for non-Frankfurt listings even on skewed values (NYSE/NASDAQ unchanged)', () => {
    expect(analyzeAdaptiveVolumeScale('NASDAQ', [120, 160, 200, 300, 42000]).adaptiveScaleActive).toBe(false);
    expect(analyzeAdaptiveVolumeScale('NYSE', [120, 160, 200, 300, 42000]).adaptiveScaleActive).toBe(false);
  });

  it('calculates deterministic robust upper bound', () => {
    const input = [100, 120, 130, 140, 1000, 1100, 1200, 90000];
    const first = analyzeAdaptiveVolumeScale('Frankfurt', input);
    const second = analyzeAdaptiveVolumeScale('Frankfurt', [...input]);
    expect(first.displayUpperBound).toBe(2420);
    expect(second.displayUpperBound).toBe(2420);
    expect(first.sampleSize).toBe(8);
    expect(first.median).toBe(140);
    expect(first.p75).toBe(1100);
    expect(first.p95).toBe(1200);
    expect(first.activationReason).toBe('combined');
  });

  it('caps only display value for outlier while preserving actual value for tooltip/metrics', () => {
    const analysis = analyzeAdaptiveVolumeScale('Frankfurt', [100, 110, 120, 130, 80000]);
    const display = toDisplayVolume(80000, analysis);
    expect(display.volumeCapped).toBe(true);
    expect(display.displayVolume).toBe(528);
    expect(80000).toBe(80000);
  });

  it('formats tooltip with actual outlier volume and visual-capping note', () => {
    expect(formatVolumeTooltipValue(80000, true)).toBe('80 000 (выброс; визуально ограничен)');
    expect(formatVolumeTooltipValue(80000, false)).toBe('80 000');
  });

  it('handles edge cases safely: empty, zero/null/non-finite, identical values, and small samples', () => {
    expect(analyzeAdaptiveVolumeScale('Frankfurt', []).hasPositiveFiniteVolume).toBe(false);
    expect(analyzeAdaptiveVolumeScale('Frankfurt', [0, null, Number.NaN, Number.POSITIVE_INFINITY]).hasPositiveFiniteVolume).toBe(false);

    const identical = analyzeAdaptiveVolumeScale('Frankfurt', [200, 200, 200, 200]);
    expect(identical.adaptiveScaleActive).toBe(false);
    expect(identical.displayUpperBound).toBe(200);

    const smallSample = analyzeAdaptiveVolumeScale('Frankfurt', [100, 10000, 200]);
    expect(smallSample.adaptiveScaleActive).toBe(false);
    expect(toDisplayVolume(1, smallSample)).toEqual({ displayVolume: 1, volumeCapped: false });
  });

  it('keeps current-price overlay volume as null and excludes it from scale statistics', () => {
    const history = buildHistoryChartData([
      {
        timestamp: '2026-08-18T00:00:00.000Z',
        interval: '1wk',
        openRaw: 20,
        highRaw: 20,
        lowRaw: 20,
        closeRaw: 20,
        openNormalized: 20,
        highNormalized: 20,
        lowNormalized: 20,
        closeNormalized: 20,
        openEur: 20,
        highEur: 20,
        lowEur: 20,
        closeEur: 20,
        volume: 300,
      },
    ], '1y', {
      timestampUtc: '2026-08-19T13:45:00.000Z',
      closeChart: 21,
      rawClose: 21,
    });
    expect(history[1]?.volumeChart).toBeNull();

    const analysis = analyzeAdaptiveVolumeScale('Frankfurt', history.map((point) => point.volumeChart));
    expect(analysis.hasPositiveFiniteVolume).toBe(true);
    expect(analysis.actualUpperBound).toBe(300);
  });

  it('derives cadence-appropriate explanatory text from actual response interval for 1y/3y/5y', () => {
    expect(getVolumeCadenceHint('1y', '1wk')).toBe('Объём по недельным свечам.');
    expect(getVolumeCadenceHint('3y', '1mo')).toBe('Объём по месячным свечам.');
    expect(getVolumeCadenceHint('5y', '1month')).toBe('Объём по месячным свечам.');
  });
});
