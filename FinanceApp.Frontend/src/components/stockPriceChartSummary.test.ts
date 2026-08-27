import { describe, expect, it } from 'vitest';
import {
  PREVIOUS_CLOSE_CHANGE_HEADING,
  SELECTED_PERIOD_CHANGE_HEADING,
  getStockPriceChartSummary,
} from './stockPriceChartSummary';

describe('getStockPriceChartSummary', () => {
  it('prefers the live previous-close baseline for 24h over the first candle', () => {
    const summary = getStockPriceChartSummary({
      historyRange: '24h',
      currentPriceDisplayValue: 236.3,
      firstHistoryClose: 230.6,
      historyHasEurConversion: true,
      liveQuote: {
        currentPriceEur: 236.3,
        changeEur: 32.3,
        normalizedCurrentPrice: 236.3,
        normalizedChange: 32.3,
        normalizedPreviousClose: 230.6,
      },
    });

    expect(summary.baselineValue).toBeCloseTo(204, 10);
    expect(summary.changeValue).toBeCloseTo(32.3, 10);
    expect(summary.changePercent).toBeCloseTo(15.8333333333, 10);
    expect(summary.baselineSource).toBe('previous-close');
    expect(summary.changeHeading).toBe(PREVIOUS_CLOSE_CHANGE_HEADING);
  });

  it('reproduces the AMZ.F regression fixture', () => {
    const summary = getStockPriceChartSummary({
      historyRange: '24h',
      currentPriceDisplayValue: 236.3,
      firstHistoryClose: 230.6,
      historyHasEurConversion: true,
      liveQuote: {
        currentPriceEur: 236.3,
        changeEur: 32.3,
        normalizedCurrentPrice: 236.3,
        normalizedChange: 32.3,
        normalizedPreviousClose: 204,
      },
    });

    expect(summary.baselineValue).toBeCloseTo(204, 10);
    expect(summary.changeValue?.toFixed(2)).toBe('32.30');
    expect(summary.changePercent).toBeCloseTo(15.8333333333, 10);
  });

  it('reconstructs the 24h baseline from stored EUR price minus stored EUR change when live quote is absent', () => {
    const summary = getStockPriceChartSummary({
      historyRange: '24h',
      currentPriceDisplayValue: 236.3,
      firstHistoryClose: 230.6,
      historyHasEurConversion: true,
      storedPriceEur: 236.3,
      storedPriceChangeEur: 32.3,
    });

    expect(summary.baselineValue).toBeCloseTo(204, 10);
    expect(summary.changeValue).toBeCloseTo(32.3, 10);
    expect(summary.baselineSource).toBe('previous-close');
    expect(summary.changeHeading).toBe(PREVIOUS_CLOSE_CHANGE_HEADING);
  });

  it('falls back to the first history close for 24h when no session snapshot baseline exists', () => {
    const summary = getStockPriceChartSummary({
      historyRange: '24h',
      currentPriceDisplayValue: 236.3,
      firstHistoryClose: 230.6,
      historyHasEurConversion: true,
    });

    expect(summary.baselineValue).toBeCloseTo(230.6, 10);
    expect(summary.changeValue).toBeCloseTo(5.7, 10);
    expect(summary.changePercent).toBeCloseTo((5.7 / 230.6) * 100, 10);
    expect(summary.baselineSource).toBe('selected-period');
    expect(summary.changeHeading).toBe(SELECTED_PERIOD_CHANGE_HEADING);
  });

  it('prefers the live previous-close baseline for today over the first candle', () => {
    const summary = getStockPriceChartSummary({
      historyRange: 'today',
      currentPriceDisplayValue: 453.79,
      firstHistoryClose: 446.91,
      historyHasEurConversion: true,
      liveQuote: {
        currentPriceEur: 453.79,
        changeEur: -18.54,
        normalizedCurrentPrice: 453.79,
        normalizedChange: -18.54,
        normalizedPreviousClose: 472.33,
      },
    });

    expect(summary.baselineValue).toBeCloseTo(472.33, 10);
    expect(summary.changeValue).toBeCloseTo(-18.54, 10);
    expect(summary.changePercent).toBeCloseTo((-18.54 / 472.33) * 100, 10);
    expect(summary.baselineSource).toBe('previous-close');
    expect(summary.changeHeading).toBe(PREVIOUS_CLOSE_CHANGE_HEADING);
  });

  it('uses the stored previous-close baseline for today when the live quote baseline is unavailable', () => {
    const summary = getStockPriceChartSummary({
      historyRange: 'today',
      currentPriceDisplayValue: 453.79,
      firstHistoryClose: 446.91,
      historyHasEurConversion: true,
      liveQuote: null,
      storedPriceEur: 453.79,
      storedPriceChangeEur: -18.54,
    });

    expect(summary.baselineValue).toBeCloseTo(472.33, 10);
    expect(summary.changeValue).toBeCloseTo(-18.54, 10);
    expect(summary.changePercent).toBeCloseTo((-18.54 / 472.33) * 100, 10);
    expect(summary.baselineSource).toBe('previous-close');
    expect(summary.changeHeading).toBe(PREVIOUS_CLOSE_CHANGE_HEADING);
  });

  it('falls back to the first history close for today when no previous-close baseline exists', () => {
    const summary = getStockPriceChartSummary({
      historyRange: 'today',
      currentPriceDisplayValue: 453.79,
      firstHistoryClose: 446.91,
      historyHasEurConversion: true,
    });

    expect(summary.baselineValue).toBeCloseTo(446.91, 10);
    expect(summary.changeValue).toBeCloseTo(6.88, 10);
    expect(summary.changePercent).toBeCloseTo((6.88 / 446.91) * 100, 10);
    expect(summary.baselineSource).toBe('selected-period');
    expect(summary.changeHeading).toBe(SELECTED_PERIOD_CHANGE_HEADING);
  });

  it('keeps other ranges anchored to the first history point even when a live previous close exists', () => {
    const summary = getStockPriceChartSummary({
      historyRange: '1w',
      currentPriceDisplayValue: 236.3,
      firstHistoryClose: 230.6,
      historyHasEurConversion: true,
      liveQuote: {
        currentPriceEur: 236.3,
        changeEur: 32.3,
        normalizedCurrentPrice: 236.3,
        normalizedChange: 32.3,
        normalizedPreviousClose: 204,
      },
    });

    expect(summary.baselineValue).toBeCloseTo(230.6, 10);
    expect(summary.changeValue).toBeCloseTo(5.7, 10);
    expect(summary.baselineSource).toBe('selected-period');
    expect(summary.changeHeading).toBe(SELECTED_PERIOD_CHANGE_HEADING);
  });

  it('avoids NaN and infinity when the baseline is zero or missing', () => {
    const zeroBaseline = getStockPriceChartSummary({
      historyRange: '24h',
      currentPriceDisplayValue: 236.3,
      firstHistoryClose: 0,
      historyHasEurConversion: true,
    });
    const missingBaseline = getStockPriceChartSummary({
      historyRange: '24h',
      currentPriceDisplayValue: 236.3,
      firstHistoryClose: null,
      historyHasEurConversion: true,
    });

    expect(zeroBaseline.changeValue).toBeCloseTo(236.3, 10);
    expect(zeroBaseline.changePercent).toBeNull();
    expect(missingBaseline.changeValue).toBeNull();
    expect(missingBaseline.changePercent).toBeNull();
  });

  it('uses the normalized baseline for non-EUR displays without double conversion', () => {
    const summary = getStockPriceChartSummary({
      historyRange: '24h',
      currentPriceDisplayValue: 472.6,
      firstHistoryClose: 460,
      historyHasEurConversion: false,
      liveQuote: {
        currentPriceEur: null,
        changeEur: null,
        normalizedCurrentPrice: 472.6,
        normalizedChange: 64.6,
        normalizedPreviousClose: 408,
      },
      storedPriceEur: 236.3,
      storedPriceChangeEur: 32.3,
    });

    expect(summary.baselineValue).toBeCloseTo(408, 10);
    expect(summary.changeValue).toBeCloseTo(64.6, 10);
    expect(summary.baselineSource).toBe('previous-close');
    expect(summary.changeHeading).toBe(PREVIOUS_CLOSE_CHANGE_HEADING);
  });
});
