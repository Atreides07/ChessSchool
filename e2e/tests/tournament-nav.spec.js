// @ts-check
const { test, expect } = require('@playwright/test');

// Регрессия на грабли #13 (тонкие клиенты): при переходе из расписания (enhanced-навигация, а НЕ F5)
// страница турнира должна инициализироваться — js/tournament.js рендерит шапку #t-hero в #t-main.
// Проверяем РЕАЛЬНЫЙ путь пользователя: клик по карточке турнира на главной.
test('турнир: страница инициализируется при переходе из расписания, а не только по F5', async ({ page }) => {
  await page.goto('/', { waitUntil: 'domcontentloaded' });

  const href = await page.$eval('a[href^="/t/"]', (a) => a.getAttribute('href')).catch(() => null);
  test.skip(!href, 'на главной нет ссылок на турниры');

  await page.click(`a[href="${href}"]`); // enhanced-навигация без перезагрузки
  // #t-hero рисует именно tournament.js после подключения к хабу — его появление = setup() отработал.
  await expect(page.locator('#t-hero')).toBeVisible({ timeout: 15_000 });
});
