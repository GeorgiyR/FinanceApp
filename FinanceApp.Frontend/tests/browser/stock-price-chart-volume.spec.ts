import { expect, test, type Page } from '@playwright/test';

type BarGeometry = {
  x: number;
  y: number;
  width: number;
  height: number;
  intersectsClip: boolean;
};

const readBarGeometry = async (page: Page) => {
  const bars = await page.evaluate<BarGeometry[]>(() => {
    const parseNumber = (value: string | null): number => {
      const parsed = Number(value);
      return Number.isFinite(parsed) ? parsed : Number.NaN;
    };

    return Array.from(document.querySelectorAll<SVGRectElement>('.recharts-bar-rectangle .recharts-rectangle'))
      .map((element) => {
        const x = parseNumber(element.getAttribute('x'));
        const y = parseNumber(element.getAttribute('y'));
        const width = parseNumber(element.getAttribute('width'));
        const height = parseNumber(element.getAttribute('height'));

        const barRect = element.getBoundingClientRect();
        const surface = element.ownerSVGElement;
        const surfaceRect = surface?.getBoundingClientRect();

        const intersectsClip = Number.isFinite(x)
          && Number.isFinite(y)
          && Number.isFinite(width)
          && Number.isFinite(height)
          && surfaceRect != null
          && barRect.left < surfaceRect.right
          && barRect.right > surfaceRect.left
          && barRect.top < surfaceRect.bottom
          && barRect.bottom > surfaceRect.top;

        return { x, y, width, height, intersectsClip };
      });
  });

  return bars.filter((bar) =>
    Number.isFinite(bar.x)
    && Number.isFinite(bar.y)
    && Number.isFinite(bar.width)
    && Number.isFinite(bar.height)
    && bar.width > 0
    && bar.height > 0);
};

test('renders visible Frankfurt long-range volume bars with payload-based tooltip timestamp', async ({ page }) => {
  await page.goto('stock-price-chart-volume-harness.html?scenario=frankfurt');
  await expect(page.getByText('Робастная шкала объёма: для долгого диапазона Frankfurt крупные выбросы визуально ограничены.')).toBeVisible();

  const oneYearBars = await readBarGeometry(page);
  expect(oneYearBars.length).toBeGreaterThan(20);
  expect(oneYearBars.every((bar) => Number.isFinite(bar.width) && Number.isFinite(bar.height))).toBeTruthy();
  expect(oneYearBars.every((bar) => bar.width >= 4)).toBeTruthy();
  expect(oneYearBars.some((bar) => bar.intersectsClip)).toBeTruthy();

  const latestOneYearBar = page.locator('.recharts-bar-rectangle .recharts-rectangle').last();
  await latestOneYearBar.hover();
  await expect(page.locator('.recharts-default-tooltip').filter({ hasText: 'Объём' }).first()).toBeVisible();
  await expect(page.locator('.recharts-default-tooltip').filter({ hasText: '204' }).first()).toBeVisible();
  await expect(page.locator('.recharts-default-tooltip').filter({ hasText: '05.01.2026' }).first()).toBeVisible();

  await page.getByText('3 года').click();
  await expect(page.getByText('Объём по месячным свечам.')).toBeVisible();
  const threeYearBars = await readBarGeometry(page);
  expect(threeYearBars.length).toBeGreaterThan(20);
  expect(threeYearBars.every((bar) => bar.width >= 4 && bar.intersectsClip)).toBeTruthy();

  await page.getByText('5 лет').click();
  const fiveYearBars = await readBarGeometry(page);
  expect(fiveYearBars.length).toBeGreaterThan(20);
  expect(fiveYearBars.every((bar) => bar.width >= 4 && bar.intersectsClip)).toBeTruthy();
});

test('keeps intraday US exchange behavior without long-range width forcing', async ({ page }) => {
  await page.goto('stock-price-chart-volume-harness.html?scenario=nyse');
  await expect(page.getByText('Робастная шкала объёма: для долгого диапазона Frankfurt крупные выбросы визуально ограничены.')).toHaveCount(0);
  await page.getByText('Сегодня').click();

  const intradayBars = await readBarGeometry(page);
  expect(intradayBars.length).toBeGreaterThan(50);
  expect(intradayBars.every((bar) => bar.width > 0 && bar.intersectsClip)).toBeTruthy();
  expect(intradayBars.some((bar) => bar.width < 4)).toBeTruthy();
});

test('renders visible intraday bars for NASDAQ without Frankfurt-specific robust message', async ({ page }) => {
  await page.goto('stock-price-chart-volume-harness.html?scenario=nasdaq');
  await expect(page.getByText('Робастная шкала объёма: для долгого диапазона Frankfurt крупные выбросы визуально ограничены.')).toHaveCount(0);
  await page.getByText('Сегодня').click();

  const intradayBars = await readBarGeometry(page);
  expect(intradayBars.length).toBeGreaterThan(50);
  expect(intradayBars.filter((bar) => bar.intersectsClip).length).toBeGreaterThan(50);
});
