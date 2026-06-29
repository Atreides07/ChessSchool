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
- ✅ Фронт/гейтинг: страница `/premium` (Paddle.js v2 checkout или dev-активация), entitlement в Arena
  ([PlayerEntitlements](../ChessSchool.Arena/Services/PlayerEntitlements.cs), кэш на ноду), бейдж
  «Premium» у текущего игрока в навигации; хук `IsPremiumAsync(sub)` для гейтинга любых фич.
- ✅ Customer Portal: `/premium/portal` → сессия hosted-портала Paddle (отмена/смена карты).
- ✅ Премиум-ценность: **разбор партий движком** + история. Завершённые арена-партии архивируются
  (Arena `FinishGame` → `IArenaGameArchiveClient` → ApiService `ArenaGameStore`, таблица `ArenaGame`).
  Страница `/me/games` (история, реплей — **бесплатно**, последние 10) и `/me/games/{id}` (реплей +
  **разбор только премиуму**: точность сторон, классификация ходов `?!/?/??`, лучшие ходы, оценка по
  ходам). Разбор считает отдельный инстанс Stockfish (`IPositionEvaluator`, чтобы не мешать ботам),
  результат кэшируется (`AnalysisJson`) и считается лениво при первом открытии. Конфиг:
  `Analysis:MoveTimeMs` (250), `Analysis:MaxPlies` (200), `Analysis:MaxConcurrent` (2). Нет бинаря
  Stockfish → разбор помечается недоступным (история/реплей работают).
- ✅ Reconcile (если вебхук не дошёл): авто-сверка после оплаты по `_ptxn` из success-URL
  (`GET /transactions` → `GET /subscriptions` → применить), кнопка «Обновить статус» и эндпоинт
  `POST /internal/subscriptions/{sub}/refresh` (по сохранённой подписке). Вытягивание не зависит от
  дедупа событий — всегда отражает актуальное состояние из API Paddle.
  - **Тайминг:** подписка у Paddle создаётся асинхронно — на момент возврата с checkout у транзакции
    `subscription_id` может быть ещё пустым. Тогда reconcile ищет подписку **по клиенту**
    (`GET /subscriptions?customer_id=…`, выбираем активную с самым поздним периодом —
    `PaddleWebhook.PickBestSubscription`), а `_ptxn` запоминается в `localStorage`, чтобы «Обновить
    статус» мог повторить сверку чуть позже. После активации кэш entitlement ноды сбрасывается
    (`IPlayerEntitlements.Invalidate`), чтобы статус подхватился сразу, без ожидания TTL.
  - **Локально без туннеля вебхук не дойдёт** (Paddle из облака не видит `localhost`) — это нормально,
    активацию делает reconcile. Полный путь с вебхуком проверяется через dev tunnel (см. ниже).

- ✅ Админка подписок (`/admin/subscriptions`, под политикой `Admin`): ручная выдача/снятие премиума и
  сдвиг срока окончания (для теста сценариев «скоро истечёт»/«истекло» и для поддержки — комп-доступ без
  оплаты). Идентификация пользователя по **e-mail** (резолв в IdP), список текущих подписок с именами.
  Это сознательный **обход провайдера**: выставленное состояние — источник истины наравне с вебхуком
  (last-write-wins; вебхук Paddle может позже его переписать). Тонкий клиент: статический каркас +
  `fetch` на `/admin/api/subscriptions*` (грабля #12), прокси в ApiService `/internal/admin/subscriptions*`
  под `X-Internal-Key`; sub→e-mail резолвится батчем в Auth (`/internal/users/by-subs`). После изменения
  кэш entitlement ноды сбрасывается (`IPlayerEntitlements.Invalidate`), статус подхватывается сразу.
  Доступна во всех окружениях (это реальный инструмент поддержки), в отличие от dev-активации `/premium`.

Премиум-статус **других** игроков не показывается (приватность) — бейдж виден только самому
пользователю в его навигации. `IsPremiumAsync(sub)` гейтит фичи по текущему пользователю.
Кто админ — решает **IdP** (ролевая модель): для админских e-mail он кладёт в токен claim `role=admin`
([AdminRoles](../ChessSchool.Auth/AdminRoles.cs)), а потребители гейтят админку политикой `RequireRole("admin")`
([Arena/Program.cs](../ChessSchool.Arena/Program.cs), `RoleClaimType="role"` в [SsoExtensions](../ChessSchool.WebAuth/SsoExtensions.cs)).
По умолчанию админ — `akhmed@outlook.com`; список расширяется в Auth через `Admin:Emails` (через запятую).
Ссылка «Админка» и все `/admin/*` видны/доступны только роли admin. После изменения состава админов
пользователю нужно перелогиниться (роль едет в токене, выдаётся при входе).

Открытие функционала под конкретные премиум-фичи — по мере продукта, через `IsPremiumAsync(sub)`.

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

Настройки лежат в user-secrets **по проекту** (вне репозитория). Нужны два проекта: `ChessSchool.ApiService`
(сервер: вебхук, портал, reconcile) и `ChessSchool.Arena` (браузерный checkout на `/premium`).

**1) ApiService — серверные ключи** (API key, секрет вебхука):

```bash
cd ChessSchool.ApiService
dotnet user-secrets set "Paddle:ApiKey"         "<server-side API key>"
dotnet user-secrets set "Paddle:WebhookSecret"  "<webhook destination secret>"
dotnet user-secrets set "Paddle:PremiumPriceId" "pri_01kw6rax9s5bfx03vyk5ccgnbz"
dotnet user-secrets set "Paddle:Environment"    "sandbox"
```

**2) Arena — клиентский токен и price_id** для страницы `/premium` (тут запускается Paddle.js Checkout):

```bash
cd ../ChessSchool.Arena
dotnet user-secrets set "Paddle:ClientToken"    "<client-side token>"
dotnet user-secrets set "Paddle:PremiumPriceId" "pri_01kw6rax9s5bfx03vyk5ccgnbz"
dotnet user-secrets set "Paddle:Environment"    "sandbox"
```

**Годовой план (опционально, для удержания).** Создай в Paddle вторую цену (annual) того же продукта со
скидкой и задай её id в Arena. Тогда на `/premium` появляется выбор «Месяц / Год» (год выбран по
умолчанию — меньше «точек продления» = меньше оттока), чекаут идёт по выбранному плану, реальные
локализованные суммы подтягиваются Paddle PricePreview. Без этого ключа — один план, как раньше.

```bash
cd ChessSchool.Arena
dotnet user-secrets set "Paddle:PremiumPriceIdAnnual" "<annual price_id>"
dotnet user-secrets set "Paddle:AnnualNote"           "−17%"   # необязательный бейдж выгоды на карточке «Год»
```

«Пауза вместо отмены» включается в самом Paddle (Customer Portal) — отдельного кода не нужно: кнопка
«Управление подпиской» уже ведёт в портал.

**Пробный период (триал) на неделю без списания.** Настраивается на самой **цене в Paddle**, кода менять
не нужно — статус `trialing` уже даёт премиум (`IsPremium`), вебхук/reconcile его маппят. Карта
привязывается при checkout, но списания в течение триала нет; по окончании — автосписание (отменить
можно в портале). Как включить:

1. Paddle → Catalog → Prices → нужная цена (`Paddle:PremiumPriceId` и/или `…Annual`) → **Trial period** =
   `7 days`. Сохранить. (Триал — свойство цены: можно сделать только на месячной, только на годовой или на обеих.)
2. Чтобы на странице `/premium` показывался баннер «Первые N дн. бесплатно» и кнопка «Попробовать N дн.
   бесплатно», задай длину триала в Arena (только для текста, на саму оплату не влияет):

   ```bash
   cd ChessSchool.Arena
   dotnet user-secrets set "Paddle:TrialDays" "7"
   ```

   Без этого ключа триал всё равно работает (его ведёт Paddle), просто на странице нет спец-текста.
3. События триала приходят теми же вебхуками (`subscription.created/updated` со статусом `trialing`);
   локально без туннеля активацию делает reconcile, как и при обычной покупке.

Так получается «7 дней бесплатно, потом платно» — стандартный триал с привязкой карты (защита от абьюза).
Если нужен триал **вообще без карты** (бесплатная неделя как грант) — это другой механизм (авто-выдача
`Trialing` на стороне приложения), сейчас не реализован; для разовой выдачи себе/тестировщику используй
админку `/admin/subscriptions` (статус «Пробный» + срок «+1 неделя»).

Проверить: `dotnet user-secrets list --project ChessSchool.ApiService` и `--project ChessSchool.Arena`.

- Ключ — через **двоеточие** (`Paddle:ClientToken`), это формат user-secrets (не `Paddle__…`, как в env).
- `Paddle:ClientToken` не секрет (уходит в браузер), но удобно держать рядом в user-secrets Arena.
- Страница `/premium` показывает Paddle-checkout, только когда у Arena заданы **и** `Paddle:ClientToken`,
  **и** `Paddle:PremiumPriceId`; иначе — кнопка dev-активации (без оплаты).
- При заданном `Paddle:WebhookSecret`/`Paddle:ApiKey` ApiService выбирает Paddle-провайдер; без них —
  dev-заглушка (премиум включается локально через `POST /internal/subscriptions/dev-activate`).

## Тест sandbox (после фазы 3)

1. Подними туннель вебхука (выше) и пропиши Notifications в Paddle.
2. `dotnet run --project ChessSchool.AppHost`, залогинься, открой `/premium`, оплати тест-картой Paddle.
3. Вебхук активирует подписку → премиум-фичи открываются. Отмена в Customer Portal → премиум снимается.

## Прод-чеклист

- `Paddle:Environment=production`, прод-ключи/секрет из KMS/Key Vault (не в коде/логах/git).
- Notifications-destination на прод-URL ApiService; события те же; проверка подписи обязательна.
- Миграции применяются отдельным шагом (см. грабля #2). Entitlement-эндпоинт закрыт внутренним ключом.
