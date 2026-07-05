// @ts-check
const { test, expect } = require('@playwright/test');

// Собираем ошибки консоли и необработанные исключения страницы.
function trackErrors(page) {
  const errors = [];
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  return errors;
}

test('главная и список трансляций грузятся без ошибок консоли', async ({ page }) => {
  const errors = trackErrors(page);
  await page.goto('/', { waitUntil: 'networkidle' });
  await page.goto('/broadcasts', { waitUntil: 'networkidle' });
  expect(errors, 'ошибки консоли: ' + errors.join(' | ')).toEqual([]);
});

// Регрессия на грабли #12/#13: при переходе со списка (а не по F5) онлайн-доски трансляции должны
// отрисоваться, а не «висеть» на «Загружаем доски…». Проверяем РЕАЛЬНЫЙ путь пользователя — клик.
test('трансляция: онлайн-доски грузятся при переходе из списка', async ({ page }) => {
  const errors = trackErrors(page);
  const scripts = [];
  page.on('response', (r) => { const u = r.url(); if (u.endsWith('.js')) scripts.push(u.split('/').pop()); });

  await page.goto('/broadcasts', { waitUntil: 'networkidle' });
  const slugs = [...new Set(
    await page.$$eval('a[href^="broadcasts/"]', (els) => els.map((e) => e.getAttribute('href')))
  )];
  test.skip(slugs.length === 0, 'в каталоге нет видимых трансляций');

  // Ищем трансляцию с онлайн-досками: раздел #bd-root рендерится только при заданном PgnUrl.
  let liveHref = null;
  for (const href of slugs) {
    await page.goto('/' + href, { waitUntil: 'domcontentloaded' });
    if (await page.$('#bd-root')) { liveHref = href; break; }
  }
  test.skip(!liveHref, 'нет трансляции с live-PGN — добавь через админку «Найти популярные» и сделай видимой');

  // Реальный путь юзера: со списка кликом (data-enhance-nav="false" → полная загрузка, скрипт исполняется).
  await page.goto('/broadcasts', { waitUntil: 'networkidle' });
  await Promise.all([
    page.waitForNavigation({ waitUntil: 'networkidle' }),
    page.click(`a[href="${liveHref}"]`),
  ]);

  // broadcast.js исполнился и заменил плейсхолдер реальными досками (а не «висит» на загрузке).
  // Имя фингерпринтится через @Assets (broadcast.<hash>.js в проде / broadcast.js в dev) — матчим префикс.
  expect(scripts.some((s) => /^broadcast\.([\w]+\.)?js$/.test(s)), 'broadcast.js не загрузился').toBe(true);
  await expect(page.locator('#bd-boards .bd-card').first()).toBeVisible();
  expect(await page.locator('#bd-boards .bd-card').count()).toBeGreaterThan(0);

  // Ориентация/окраска доски не инвертированы: a1 тёмная, светлое поле справа-снизу (h1).
  // Ячейки мини-доски идут в порядке r8..r1, a..h → 0=a8, 7=h8, 56=a1, 63=h1.
  const corners = await page.$$eval('#bd-boards .bd-card:first-child .bd-msq', (els) => {
    const tone = (i) => (els[i].classList.contains('d') ? 'dark' : 'light');
    return { a8: tone(0), h8: tone(7), a1: tone(56), h1: tone(63) };
  });
  expect(corners, 'цвета клеток инвертированы').toEqual({ a8: 'light', h8: 'dark', a1: 'dark', h1: 'light' });

  // Все 64 клетки одинакового размера (грабля: без grid-template-rows ряды разной высоты).
  const sizes = await page.$$eval('#bd-boards .bd-card:first-child .bd-msq', (els) =>
    els.map((e) => { const r = e.getBoundingClientRect(); return { w: r.width, h: r.height }; }));
  const spread = (vals) => Math.max(...vals) - Math.min(...vals);
  expect(spread(sizes.map((s) => s.w)), 'ширины клеток неодинаковы').toBeLessThanOrEqual(1);
  expect(spread(sizes.map((s) => s.h)), 'высоты клеток неодинаковы').toBeLessThanOrEqual(1);
  expect(Math.abs(sizes[0].w - sizes[0].h), 'клетка не квадратная').toBeLessThanOrEqual(1);

  // Клик по доске открывает оверлей с навигацией по ходам.
  await page.locator('#bd-boards .bd-card').first().click();
  await expect(page.locator('#bd-overlay')).toBeVisible();
  await expect(page.locator('#bd-board .sq')).toHaveCount(64);

  expect(errors, 'ошибки консоли: ' + errors.join(' | ')).toEqual([]);
});
