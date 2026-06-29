// @ts-check
const { defineConfig } = require('@playwright/test');

// Смоук-тесты гоняются против УЖЕ ЗАПУЩЕННОГО приложения (AppHost). Базовый URL — внешний адрес Arena
// из дашборда Aspire (НЕ Kestrel-порт — иначе ломается redirect_uri, см. CLAUDE.md). Переопределяется ARENA_URL.
module.exports = defineConfig({
  testDir: './tests',
  timeout: 30_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.ARENA_URL || 'https://localhost:7167',
    ignoreHTTPSErrors: true, // дев-сертификат
    headless: true,
    viewport: { width: 1280, height: 800 },
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
});
