// @ts-check
const { test, expect } = require('@playwright/test');

// Регрессия на грабли #22 (тонкие клиенты, арена): часы игрока не должны «замирать»/дёргаться.
// Ключ фикса в js/tournament.js — КАЖДЫЙ .js-clock ведёт отсчёт от СВОЕЙ базы data-at (а не от общего
// stateAt, который сбрасывается на любом пуше турнира), и tickClocks обновляет текст в месте.
//
// Проверяем детерминированно, без живой партии: на странице турнира крутится tickClocks (setInterval 250мс)
// по всем .js-clock. Вставляем синтетические активные часы с data-at в прошлом и убеждаемся, что текст
// считается ОТ data-at. Старое поведение (общий stateAt ≈ now) показало бы 01:00 — тест бы упал.
test('арена: js-clock ведёт отсчёт от собственной data-at, а не от stateAt', async ({ page }) => {
  await page.goto('/', { waitUntil: 'domcontentloaded' });

  const href = await page.$eval('a[href^="/t/"]', (a) => a.getAttribute('href')).catch(() => null);
  test.skip(!href, 'на главной нет турниров — tickClocks не запущен');

  await page.click(`a[href="${href}"]`);
  await expect(page.locator('#t-hero')).toBeVisible({ timeout: 15_000 }); // setup() отработал → clockTimer идёт

  // Активные часы: остаётся 60с, база отсчёта — 3с назад. tickClocks обязан показать ~00:57.
  await page.evaluate(() => {
    const s = document.createElement('span');
    s.className = 'js-clock';
    s.id = 'probe-clock';
    s.dataset.ms = '60000';
    s.dataset.at = String(Date.now() - 3000);
    s.dataset.active = '1';
    document.body.appendChild(s);
  });

  await page.waitForTimeout(400); // дать сработать tickClocks (тик 250мс)
  const text = await page.locator('#probe-clock').textContent();
  await page.evaluate(() => document.getElementById('probe-clock')?.remove());

  // 60с − 3с = 57с (±1с на дрожание таймера). Если бы отсчёт шёл от stateAt (≈now) — было бы 01:00/00:59.
  expect(['00:56', '00:57', '00:58']).toContain(text);
});
