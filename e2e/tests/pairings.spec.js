// @ts-check
const { test, expect } = require('@playwright/test');

// Собираем ошибки консоли и необработанные исключения страницы.
function trackErrors(page) {
  const errors = [];
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  return errors;
}

test('жеребьёвка: страница импорта грузится без ошибок консоли', async ({ page }) => {
  const errors = trackErrors(page);
  await page.goto('/pairings', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('#pr-drop', { timeout: 10_000 });
  expect(errors, 'ошибки консоли: ' + errors.join(' | ')).toEqual([]);
});

// Регрессия на грабли #13: при переходе из меню (enhanced-навигация, а НЕ F5) Blazor морфит DOM, и
// обработчики импорта раньше не привязывались — кнопки/файл «не работали», помогал только refresh.
// Проверяем РЕАЛЬНЫЙ путь: клик по пункту меню и фактическую привязку обработчиков на #pr-file/#pr-url-form.
test('жеребьёвка: импорт работает при переходе из меню, а не только по F5', async ({ page }) => {
  const errors = trackErrors(page);

  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.click('a.ar-link[href="pairings"]'); // enhanced-навигация без перезагрузки
  await page.waitForSelector('#pr-drop', { timeout: 10_000 });

  const bound = await page.evaluate(() => ({
    ready: document.getElementById('pr-root')?.dataset.prReady,
    fileChange: typeof document.getElementById('pr-file')?.onchange === 'function',
    urlSubmit: typeof document.getElementById('pr-url-form')?.onsubmit === 'function',
  }));

  expect(bound.ready, 'инициализация #pr-root не отметилась').toBe('1');
  expect(bound.fileChange, 'обработчик выбора файла не привязан после enhanced-навигации').toBe(true);
  expect(bound.urlSubmit, 'обработчик отправки ссылки не привязан после enhanced-навигации').toBe(true);
  expect(errors, 'ошибки консоли: ' + errors.join(' | ')).toEqual([]);
});
