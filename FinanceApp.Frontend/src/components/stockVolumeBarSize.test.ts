import { describe, expect, it } from 'vitest';
import { MAX_VOLUME_BAR_SIZE_PX, MIN_VOLUME_BAR_SIZE_PX, resolveVolumeBarSize } from './stockVolumeBarSize';

describe('resolveVolumeBarSize', () => {
  it('returns visible clamped width for Frankfurt-like 1y weekly point count', () => {
    const size = resolveVolumeBarSize({
      containerWidth: 960,
      positivePointCount: 53,
      historyRange: '1y',
    });

    expect(size).toBeGreaterThanOrEqual(MIN_VOLUME_BAR_SIZE_PX);
    expect(size).toBeLessThanOrEqual(MAX_VOLUME_BAR_SIZE_PX);
  });

  it('uses width fallback before container measurement', () => {
    const size = resolveVolumeBarSize({
      containerWidth: 0,
      positivePointCount: 53,
      historyRange: '1y',
    });

    expect(size).toBeGreaterThanOrEqual(MIN_VOLUME_BAR_SIZE_PX);
    expect(size).toBeLessThanOrEqual(MAX_VOLUME_BAR_SIZE_PX);
  });

  it('supports 3y/5y monthly range sizing and narrow-container clamping', () => {
    expect(resolveVolumeBarSize({
      containerWidth: 960,
      positivePointCount: 36,
      historyRange: '3y',
    })).toBeGreaterThanOrEqual(MIN_VOLUME_BAR_SIZE_PX);

    expect(resolveVolumeBarSize({
      containerWidth: 960,
      positivePointCount: 60,
      historyRange: '5y',
    })).toBeGreaterThanOrEqual(MIN_VOLUME_BAR_SIZE_PX);

    expect(resolveVolumeBarSize({
      containerWidth: 240,
      positivePointCount: 60,
      historyRange: '5y',
    })).toBe(MIN_VOLUME_BAR_SIZE_PX);
  });

  it('does not force explicit bar width for short/intraday ranges', () => {
    expect(resolveVolumeBarSize({
      containerWidth: 960,
      positivePointCount: 300,
      historyRange: 'today',
    })).toBeUndefined();
    expect(resolveVolumeBarSize({
      containerWidth: 960,
      positivePointCount: 53,
      historyRange: '1w',
    })).toBeUndefined();
  });
});
