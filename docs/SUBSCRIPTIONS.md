# Подписки (B2C-премиум) на Paddle Billing

Платная премиум-подписка игрока. Источник истины о статусе — `ApiService` (Postgres); Paddle шлёт
вебхуки, потребители (Arena/Web) спрашивают entitlement по внутреннему ключу. Карты у нас не ходят —
оплата только через hosted Checkout/Portal Paddle (PCI на стороне Paddle). Соблюдает принципы
[CLAUDE.md](../CLAUDE.md): мультисервер, идемпотентность, секреты из конфига/KMS.

## Поток

1. Игрок на странице `/premium` запускает **Paddle.js Checkout** (price_id плана, `custom_data.user_sub`).
2. Оплата проходит у Paddle → Paddle шлёт **вебхук** `subscription.*` на `ApiService /webhooks/paddle`.
3. ApiService **проверяет подпись** (`Paddle-Signature`), идемпотентно обновляет статус подписки.
4. Arena/Web проверяют `IsPremium(sub)` (entitlement из ApiService, кэш на ноду) и гейтят фичи.

## Состояние реализации

- ✅ Ядро: модель `Subscription`/идемпотентность, `SubscriptionService`, entitlement-эндпоинт, dev-заглушка.
- ✅ Paddle: `PaddleBillingProvider`, верификация вебхука, маппинг событий, `POST /webhooks/paddle`.
- ⏳ Фаза 3: страница `/premium` + Paddle.js + Customer Portal + гейтинг премиума в Arena.

## Настройка Paddle sandbox (то, что нужно сделать в дашборде)

Дашборд: <https://sandbox-vendors.paddle.com>. Всё ниже — в **sandbox**.

1. **Price** — уже есть: `pri_01kw6rax9s5bfx03vyk5ccgnbz` (Catalog → Products). Это `Paddle:PremiumPriceId`.
2. **Server-side API key:** Developer tools → Authentication → **API keys** → New key (права на чтение
   подписок) → значение в `Paddle:ApiKey`.
3. **Client-side token:** Developer tools → Authentication → **Client-side tokens** → New token →
   значение в `Paddle:ClientToken` (он не секрет — уходит в браузер на странице `/premium`).
4. **Webhook (Notifications):** Developer tools → **Notifications** → New destination:
   - URL = публичный адрес ApiService + `/webhooks/paddle` (см. ниже про туннель);
   - события: `subscription.created`, `subscription.activated`, `subscription.updated`,
     `subscription.canceled`, `subscription.past_due`, `subscription.paused`;
   - после создания скопируй **secret key** назначения → `Paddle:WebhookSecret`.

### Публичный URL для вебхука локально (dev tunnel)

Paddle должен достучаться до локального ApiService (Kestrel https `7551`). Подними туннель на этот порт
и используй его URL + `/webhooks/paddle` как адрес назначения в Notifications:

```bash
devtunnel create chess-webhook
devtunnel port create chess-webhook -p 7551
devtunnel access create chess-webhook --anonymous
devtunnel show chess-webhook            # URL → вставить в Paddle Notifications как .../webhooks/paddle
devtunnel host chess-webhook            # держать запущенным
```

## Секреты и конфиг (значения вводишь сам — мне не присылай; `.env` не читаю)

Положи секреты в user-secrets `ChessSchool.ApiService` (имена — ниже):

```bash
cd ChessSchool.ApiService
dotnet user-secrets set "Paddle:ApiKey"        "<server-side API key>"
dotnet user-secrets set "Paddle:WebhookSecret" "<webhook destination secret>"
dotnet user-secrets set "Paddle:PremiumPriceId" "pri_01kw6rax9s5bfx03vyk5ccgnbz"
dotnet user-secrets set "Paddle:Environment"   "sandbox"
```

Клиентский токен и price_id для страницы `/premium` (Arena/Web, фаза 3) — туда же по месту:
`Paddle:ClientToken`, `Paddle:PremiumPriceId`, `Paddle:Environment=sandbox`.

При заданном `Paddle:WebhookSecret`/`Paddle:ApiKey` ApiService выбирает Paddle-провайдер; без них —
dev-заглушка (премиум включается локально через `POST /internal/subscriptions/dev-activate`).

## Тест sandbox (после фазы 3)

1. Подними туннель вебхука (выше) и пропиши Notifications в Paddle.
2. `dotnet run --project ChessSchool.AppHost`, залогинься, открой `/premium`, оплати тест-картой Paddle.
3. Вебхук активирует подписку → премиум-фичи открываются. Отмена в Customer Portal → премиум снимается.

## Прод-чеклист

- `Paddle:Environment=production`, прод-ключи/секрет из KMS/Key Vault (не в коде/логах/git).
- Notifications-destination на прод-URL ApiService; события те же; проверка подписи обязательна.
- Миграции применяются отдельным шагом (см. грабля #2). Entitlement-эндпоинт закрыт внутренним ключом.
