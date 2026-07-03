# PROJECT.md — специфика репозитория ChessSchool

Подключается импортом из [CLAUDE.md](CLAUDE.md) (универсальный инженерный стандарт). Здесь — всё, что
привязано к конкретному продукту и стеку: описание, калибровка принципов, команды, карта решения,
архитектурные решения, грабли, секреты.

## Что это

**ChessSchool** — платформа для шахматных школ на базе idchess: учёт партий и рейтинга учеников,
страница прогресса для родителей по ссылке, и **онлайн-игра в реальном времени** с прицелом на
масштаб (целевой ориентир — до 1 млн одновременных партий). Подробное ТЗ:
[docs/TЗ_ChessSchool_Platform.md](docs/T%D0%97_ChessSchool_Platform.md).

Стек: **.NET 10**, **Blazor Web App (SSR ради SEO)**, оркестрация — **.NET Aspire** (локально без
Docker). Онлайн-игра — **Microsoft Orleans** (грейн на партию) + **SignalR**. Авторизация — отдельный
переиспользуемый **IdP на OpenIddict 7.5** (OIDC/JWT/JWKS), как Google Auth (явное требование заказчика).

Известный технический долг и отложенные задачи — [docs/TODO.md](docs/TODO.md).
Развёртывание в проде (инфраструктура, конфиг, миграции, Orleans/Redis, чек-листы) —
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md). Нагрузочная модель и железо под 100k/1M онлайн —
[docs/CAPACITY_PLANNING.md](docs/CAPACITY_PLANNING.md). Продуктовые метрики — [docs/PRODUCT_METRICS.md](docs/PRODUCT_METRICS.md).
Функциональные требования к игре на доске (ходы, часы, предходы, подбор, завершение) —
[docs/BOARD_GAME_REQUIREMENTS.md](docs/BOARD_GAME_REQUIREMENTS.md). Демо за dev tunnels (логин+игра,
шаринг тестировщикам) — [docs/DEMO_TUNNELS.md](docs/DEMO_TUNNELS.md). Подписки (B2C-премиум на Paddle
Billing: модель, вебхук, настройка sandbox) — [docs/SUBSCRIPTIONS.md](docs/SUBSCRIPTIONS.md).
Архитектурные решения Арены (грейн-на-турнир, co-hosting Orleans+Blazor, push без SignalR-backplane,
тонкие клиенты, дешёвые листинги, боты, безопасность) — [docs/ARENA_ARCHITECTURE.md](docs/ARENA_ARCHITECTURE.md).

## Калибровка универсальных принципов под этот продукт

Как применять принципы из [CLAUDE.md](CLAUDE.md) именно здесь:

- **Мультисерверность и производительность — жёсткий приоритет** (не «по возможности»): целевой масштаб —
  до 1 млн одновременных партий, ~100k ходов/с по кластеру. Вариант, который работает на одной ноде, но
  ломается на нескольких, — неправильный, даже если «локально работает». Горячие пути (матчмейкинг, ходы,
  листинги, аутентификация, рассылки) проектируются под масштаб сразу.
- **Конкретные механизмы мультисерверности:** состояние партии/турнира — Orleans-грейн (единственный
  владелец, однопоточный доступ), персист в общий стор; реалтайм — SignalR Redis-backplane + Orleans
  Redis-clustering; сессии — распределённый ticket-store; ключи — общий DataProtection-keyring. Всё
  переключается по наличию строки подключения `redis` (см. §Масштабирование онлайн-игры).
- **SEO:** стек выбран ради него — **Blazor SSR**, контент в HTML на первом ответе (лендинг, `/p/{token}`
  родителю, страницы/списки турниров). Закрытый ЛК — вторично, уместно `noindex`.
- **Локализация (RU/EN):** инфраструктура общая — `AddChessSchoolLocalization` в
  [ServiceDefaults](ChessSchool.ServiceDefaults/Extensions.cs); строки — статический `Loc`
  ([Arena](ChessSchool.Arena/Loc.cs)/[Web](ChessSchool.Web/Loc.cs)): `Loc.T` для UI, `Loc.Tr` для данных,
  `Loc.IsEn` для ветвлений; культура из `CultureInfo.CurrentUICulture` (её ставит RequestLocalization);
  язык в URL через `?culture=`, эндпоинт `/lang` ставит cookie; `<html lang>` и `hreflang` — в `App.razor`.
- **Дизайн-система** — [ChessSchool.Design](ChessSchool.Design/wwwroot/css/design.css): единые токены/компоненты, светлый минимализм idChess.

## Команды

```bash
dotnet run --project ChessSchool.AppHost   # запуск всего (откроется дашборд Aspire). Нужен Docker/Podman (Postgres).
dotnet test                                # юнит + интеграционные (вкл. полный старт AppHost; ~до 180с)
dotnet format                              # анализатор стиля/кода
dotnet build                               # сборка решения
```

> **Требуется контейнер-рантайм (Docker/Podman).** БД — PostgreSQL для всех окружений; Aspire
> поднимает контейнер Postgres локально. Без рантайма не стартуют AppHost и интеграционный
> `WebTests`. Быстрые тесты (`ApiServiceTests` на EF InMemory, юниты) работают без Docker:
> `dotnet test --filter "FullyQualifiedName!~WebTests"`.

Точка входа после запуска — `webfrontend` (бери внешний URL **из дашборда Aspire**, не Kestrel-порт —
см. гочу про redirect_uri ниже). Маршруты: `/` лендинг, `/school` ЛК школы, `/students/{id}` профиль,
`/p/{token}` публичный профиль родителю, `/attribution` очередь атрибуции, `/play` онлайн-партия.

После изменений в коде/конфиге **всегда** запускай `dotnet test` и `dotnet format`. Если изменения
не покрыты тестами — добавь тесты. (Общее правило самопроверки — в CLAUDE.md; здесь — конкретные команды.)

## Карта решения

| Проект | Роль |
|---|---|
| `ChessSchool.AppHost` | Оркестратор Aspire — [AppHost.cs](ChessSchool.AppHost/AppHost.cs) связывает все сервисы |
| `ChessSchool.Auth` | IdP (OpenIddict): authorization code + PKCE, refresh, JWKS на `/.well-known/jwks` |
| `ChessSchool.ApiService` | Доменный API: школы, ученики, рейтинг (Elo), архив партий, шаринг |
| `ChessSchool.GameServer` | Онлайн-игра: Orleans-силос (живые партии) + SignalR-хаб + матчмейкинг |
| `ChessSchool.Arena` | B2C арена-турниры: co-hosted Orleans + Blazor, боты на Stockfish |
| `ChessSchool.Web` | Blazor Web App: ЛК, профили, публичная страница, онлайн-доска |
| `ChessSchool.WebAuth` | Подключение веб-приложений к IdP (`AddOpenIdConnect`, ticket-store) |
| `ChessSchool.Design` | Дизайн-система (RCL): токены/компоненты, фигуры Cburnett |
| `ChessSchool.Contracts` | Общие DTO и контракты SignalR |
| `ChessSchool.ServiceDefaults` | Health-checks, OpenTelemetry, service discovery |
| `ChessSchool.Tests` | xUnit + bunit + Aspire.Hosting.Testing + Orleans.TestingHost |

### Масштабирование онлайн-игры
Каждая партия — Orleans-**грейн** (однопоточный доступ к состоянию → нет гонок без локов).
Завершённые партии деактивируются (в RAM только активные). Транспорт (SignalR) и состояние
(Orleans-силосы) масштабируются независимо. Распределённые провайдеры **переключаются по наличию строки подключения `redis`** (см.
[GetRedisConnectionString](ChessSchool.ServiceDefaults/Extensions.cs)): есть Redis → Orleans
Redis-clustering + Redis grain storage, SignalR Redis-backplane, общий DataProtection-keyring,
распределённый ticket-store; нет → dev-путь (localhost-кластер, in-memory storage, SignalR in-proc,
файловый keyring/ticket-store). AppHost локально поднимает контейнер Redis ([AppHost.cs](ChessSchool.AppHost/AppHost.cs)),
поэтому распределённые пути работают и проверяются и локально. Персист доменных данных → **PostgreSQL**
(managed в проде). dev и прод на одном провайдере БД — схема через EF-миграции.

## Архитектурные решения

- **Регистрация с подтверждением e-mail + МЯГКИЙ гейт** ([ChessSchool.Auth/Program.cs](ChessSchool.Auth/Program.cs)):
  регистрация создаёт пользователя неподтверждённым, шлёт письмо (`/account/confirm?token=…`) и **сразу пускает**
  (не блокирует) — ценность доступна немедленно, подтверждение просим nudge-баннером. Статус едет в токен
  **claim `email_verified`** (authorize/userinfo/cookie); приложения закрывают **чувствительное** до подтверждения.
  Матрица доступа (см. правило в CLAUDE.md): играть/смотреть — можно неподтверждённому; **оплата/премиум**,
  идентичность — после подтверждения. В Арене: политика `ConfirmedEmail` на премиум + баннер в
  [MainLayout](ChessSchool.Arena/Components/Layout/MainLayout.razor); claim мапится в
  [WebAuth](ChessSchool.WebAuth/SsoExtensions.cs) (`MapUniqueJsonKey email_verified`). Переход по ссылке подтверждает
  и логинит. **Смена e-mail:** НЕподтверждённый адрес — `/account/change-email` меняет сразу (исправить опечатку).
  **Подтверждённый** — verify-new-before-switch: адрес не меняется, пока владение новым не доказано ссылкой
  (`AppUser.PendingEmail`, purpose `ChangeEmail`, `/account/confirm-email-change`); ссылка — на новый адрес,
  уведомление — на старый; на confirm перевыпускается security-stamp. Одноразовые токены — [EmailTokenService](ChessSchool.Auth/EmailTokenService.cs)
  (в БД только SHA-256-хэш, срок 24ч, гасятся при использовании/перевыпуске). Почта — [IEmailSender](ChessSchool.Auth/Email/EmailSender.cs):
  есть `Email:Smtp:Host` → MailKit-SMTP (локально **mailpit** из AppHost; прод — реальный SMTP), нет → лог-фолбэк.
  Существующие аккаунты grandfather-нуты миграцией `AddEmailConfirmation`.
- **Пароли (NIST 800-63B)** ([Password.cs](ChessSchool.Auth/Password.cs)): решает длина (min из `Auth:Password:MinLength`,
  дефолт 8; max 128 против DoS), без обязательной композиции/ротации; проверка по базе утечек **HIBP** (k-anonymity —
  наружу только 5-символьный префикс SHA-1; `Auth:Password:CheckPwned`, дефолт вкл, **fail-open** при недоступности API;
  в тестах выключено). **Constant-time логин**: при отсутствии пользователя всё равно считаем dummy-хэш (анти-энумерация
  по таймингу). Проверка длины+утечки — на регистрации и на сбросе пароля.
- **Сброс пароля (OWASP)** ([Program.cs](ChessSchool.Auth/Program.cs)): `/account/forgot` — **нейтральный** ответ
  «письмо отправлено, если аккаунт есть» (анти-энумерация), rate-limit `email-send`; `/account/reset?token=…` —
  форма нового пароля. Токен `ResetPassword` одноразовый, живёт **1ч** (в БД только хэш, [EmailTokenService](ChessSchool.Auth/EmailTokenService.cs)),
  rate-limit `auth` (анти-перебор токена). При сбросе: пароль проходит NIST+HIBP; `EmailConfirmed=true` (ссылка из письма
  доказывает владение адресом); **отзыв всех OIDC-токенов/разрешений** пользователя (`IOpenIddictTokenManager`/`…AuthorizationManager` —
  краденые access/refresh умирают); **security-stamp** гасит и cookie-сессии IdP на всех устройствах; **письмо-уведомление** владельцу о смене пароля.
- **Security-stamp — логаут на всех устройствах** ([AppUser.SecurityStamp](ChessSchool.Auth/Data/AppUser.cs)): метка едет в
  claim cookie-сессии IdP; `OnValidatePrincipal` ([Program.cs](ChessSchool.Auth/Program.cs)) сверяет её с БД и разлогинивает
  при несовпадении. Смена пароля перевыпускает метку → прочие сессии отклоняются. Интервал проверки
  `Auth:SecurityStamp:ValidateMinutes` (дефолт 5; `0` = каждый запрос). Миграция `AddSecurityStamp` (grandfather).
- **Rate-limiting переключается по Redis** ([RedisRateLimiting.cs](ChessSchool.Auth/RedisRateLimiting.cs)): есть Redis →
  распределённый `RedisFixedWindowRateLimiter` (общий счётчик на все ноды, атомарный Lua `INCRBY`+`PEXPIRE`, fail-open),
  нет → in-memory (dev/одна нода). **Аудит auth-событий** — таблица `AuthEvents` + [AuthAudit](ChessSchool.Auth/AuthAudit.cs);
  метрики `chessschool.auth.events` ([AuthMetrics](ChessSchool.Auth/AuthMetrics.cs)) через OTel для алертинга;
  вход с нового IP → письмо-уведомление владельцу (`NewSignIn`) + событие `NewDeviceLogin`.
- **Полный реестр политик/настроек безопасности — [docs/SECURITY.md](docs/SECURITY.md)** (статус, место в коде,
  параметры, компромиссы, отложенное). Отложено (OWASP/NIST): MFA, готовые пороговые правила алертинга (в системе мониторинга).
- **Рейтинг** — Elo за интерфейсом `IRatingService` ([RatingService.cs](ChessSchool.ApiService/Services/RatingService.cs)),
  заложен переход на Glicko-2.
- **Атрибуция тренировочных партий**: партии без чек-ина ученика идут в очередь тренера
  (`/attribution`) и **не влияют на рейтинг** до подтверждения.
- **`/play` — «тонкий» клиент** ([Play.razor](ChessSchool.Web/Components/Pages/Play.razor)):
  статический SSR-каркас (без `@rendermode InteractiveServer`), правила/оптимистичный ход через
  `chess.js`, синхронизация — браузерный SignalR прямо к `/gamehub` GameServer-а (токен с
  `/api/game-token`). Снимает «состояние на игрока» с веб-сервера и переживает плохую сеть
  (reconnect → ресинк через `JoinGame`). Серверный Blazor-`Chessboard` остался только для Arena.
- **Боты Arena** — серверный Stockfish через UCI ([StockfishEngine.cs](ChessSchool.Arena/Services/StockfishEngine.cs)),
  singleton, запросы сериализуются семафором. Нет бинаря → ход случайным легальным. Локально:
  `brew install stockfish`.
- **Дизайн-система ChessSchool.Design** — светлый минимализм idChess (акцент `#2b6ef2`), единые
  токены/компоненты в `wwwroot/css/design.css`. **Bootstrap удалён** (его `.row`-грид конфликтовал
  с утилитой `.row`). Фигуры — набор Cburnett (SVG), не глифы шрифта.

## Грабли (подтверждены на практике — не наступать снова)

> **Правило ведения этого списка (обязательное).** Если что-то не заработало из-за граблей — особенно
> уже описанных здесь, на которые всё равно наступили, — зафиксируй это в данном разделе: новый
> пронумерованный пункт (симптом → причина → лечение) или явное усиление существующего. Поддержание
> списка актуальным — часть выполнения задачи, наравне с тестами и форматом.

1. **Razor: строковый параметр компонента биндить ВСЕГДА через `@`.** `Fen="g.Fen"` передаёт литерал
   `"g.Fen"`, а не значение (для `bool` без `@` выражение вычисляется — отсюда коварство). Правильно:
   `Fen="@g.Fen"`.
2. **БД — PostgreSQL для всех окружений** (SQLite убран). Схема версионируется EF-миграциями.
   Применение: режим `migrate` (`dotnet ChessSchool.Auth.dll migrate` — применил и вышел, для прод-Job)
   или авто-миграция на старте при `Database:MigrateAtStartup` (по умолчанию = Development); для InMemory
   в тестах — `EnsureCreated()`. Прод-сертификаты IdP (вне Development) грузятся из конфигурации
   `OpenIddict:SigningCertificate`/`:EncryptionCertificate` ([Certificates.cs](ChessSchool.Auth/Certificates.cs)).
   Генерация миграций без Docker — через design-time фабрики (`AuthDbContextFactory`/`SchoolDbContextFactory`):
   `dotnet ef migrations add <Name> -p ChessSchool.Auth -s ChessSchool.Auth -o Migrations`.
   Фабрика Auth обязана звать `UseOpenIddict()`, иначе таблицы OpenIddict не попадут в миграцию.
3. **Два Orleans-силоса (GameServer и Arena) на одной машине конфликтуют** портами/clusterId.
   Разведены: GameServer `11111/30000 clusterId=chessschool-game`, Arena `11112/30001 clusterId=chessschool-arena`.
4. **IdP: JWK строить только из публичных параметров** (`n,e`) — `ConvertFromRSASecurityKey` на ключе
   с приватной частью утекает `d,p,q,...` в JWKS. Защищено `JwksSecurityTests`.
5. **CORS GameServer гейтится по окружению** ([GameServer/Program.cs](ChessSchool.GameServer/Program.cs)):
   Development — любой origin (порты Aspire динамические), прод — строгий список из `Cors:Origins`.
   any-origin + `AllowCredentials()` в проде = дыра (чужой сайт дёргает хаб от имени пользователя).
6. **redirect_uri / порты под Aspire:** внешний порт веба ≠ Kestrel-порт. Заходить по **внешнему URL из
   дашборда** (совпадает с seeded redirect_uri). Иначе OIDC строит redirect_uri с Kestrel-портом → `invalid_request`.
7. **HTTP 431 на всех localhost-страницах = раздутая auth-cookie.** Фикс в
   [SsoExtensions](ChessSchool.WebAuth/SsoExtensions.cs): server-side ticket-store (в cookie только ключ) +
   `Kestrel MaxRequestHeadersTotalSize=256KB`. Ticket-store переключаемый по Redis: есть →
   `DistributedCacheTicketStore` (общий для всех нод, обязателен за балансировщиком); нет →
   `FileSystemTicketStore` (тикет шифруется DataProtection, `keys/auth-tickets`, переживает рестарт
   одной ноды). DataProtection-ключи тоже общие при Redis ([AddChessSchoolDataProtection](ChessSchool.ServiceDefaults/Extensions.cs)) —
   иначе нода не расшифрует cookie/тикет, выданный другой.
8. **Health-checks AppHost:** `WithHttpHealthCheck` по https с dev-сертификатом вешает `WaitFor`-каскад.
   Health маппится всегда (не только Development), интеграционный тест ждёт состояния `Running`, не `Healthy`.
9. **Arena-турнир переживает деактивацию грейна.** Мета+таблица персистятся в grain storage `"arena"`
   ([Program.cs](ChessSchool.Arena/Program.cs)): есть Redis → `AddRedisGrainStorage("arena")` (состояние
   переживает рестарт/масштабирование силосов), нет → `AddMemoryGrainStorage("arena")` (dev). Грейн сам
   выводит мету из своего id через [ArenaSchedule](ChessSchool.Arena/Services/ArenaSchedule.cs)
   (`EnsureConfigured`), а пока турнир идёт держит себя живым (`DelayDeactivation`). Активные доски НЕ
   персистятся (при реактивации игроки переспариваются). **Тестовый силос обязан звать
   `AddMemoryGrainStorage("arena")`**, иначе `[PersistentState("tournament","arena")]` не резолвится.
10. **Orleans-кластеризация переключается по Redis** ([Arena](ChessSchool.Arena/Program.cs)/[GameServer](ChessSchool.GameServer/Program.cs)/Program.cs):
    есть `redis` → `UseRedisClustering` + `ConfigureEndpoints` (несколько нод в одном кластере, грейн —
    единственная активация); нет → `UseLocalhostClustering` (dev). Два силоса по-прежнему разведены по
    портам/clusterId (грабля #3). SignalR — `AddStackExchangeRedis` при наличии Redis, иначе in-proc.
11. **Тик турнира Arena: таймер + reminder-«воскрешение».** Партии ведёт мелкий `RegisterGrainTimer`
    (500 мс), но он живёт только в активном грейне на одной ноде. При Redis включается `UseRedisReminderService`,
    и грейн (`IRemindable`, [ArenaGrains.cs](ChessSchool.Arena/Grains/ArenaGrains.cs)) регистрирует reminder
    `arena-tick` (период 1 мин — минимум Orleans): при внезапной потере ноды он воскрешает грейн на другой,
    тот восстанавливает состояние из grain storage и возобновляет тик. Reminder регистрируется ТОЛЬКО при
    `ArenaRuntimeOptions.RemindersEnabled` (есть Redis-сервис); тестовый силос — `UseInMemoryReminderService`.
    Push-обновления (`ArenaNotifier`) при Redis идут через pub/sub-канал `arena:notify` (зритель на любой
    ноде получает обновление турнира, чей грейн на другой ноде), иначе внутрипроцессно.
12. **Исходящий HTTP из рендера Blazor-компонента зависает** — `await client.SendAsync(...)` к другому
    сервису из `OnInitializedAsync` НЕ возвращается и НЕ упирается в таймаут (поток рендерера
    заблокирован). Воспроизводилось и в `@rendermode InteractiveServer` (prerender и контур), и
    периодически даже в статическом SSR-компоненте (authed-документ не отдавался). Тот же вызов из
    обычного request-контекста (minimal-API) — мгновенно (пробник: detail 58 мс). **Надёжное лечение —
    чистый тонкий клиент**: страница ([GameReview.razor](ChessSchool.Arena/Components/Pages/GameReview.razor)) —
    статический каркас БЕЗ серверного HTTP, все данные браузер тянет `fetch`'ем с обычных minimal-API
    эндпоинтов (`GET /api/me/games/{id}` — позиции/мета, `…/analysis` — разбор Stockfish), доска рисуется
    из FEN без внешних библиотек (НЕ грузить chess.js с esm.sh — внешний CDN падал → пустая доска).
    Правило: НЕ делай исходящий HTTP в лайфсайкле Blazor-компонента; выноси в minimal-API + браузерный fetch.
    Ссылку на такую страницу из списка помечай `data-enhance-nav="false"` — иначе enhanced-навигация не
    исполнит её `<script>` (см. комментарий в [App.razor](ChessSchool.Arena/Components/App.razor)).
13. **Инлайновый `<script>` на странице, открываемой enhanced-навигацией, НЕ исполняется.** Blazor
    enhanced-nav подменяет DOM без полной перезагрузки и не запускает вставленные `<script>` (даже на
    статических SSR-страницах). Симптом: тонкий клиент «висит» в исходном состоянии — сетка досок
    трансляции осталась на «Загружаем доски…» при переходе со списка (а при F5 — работала). **Лечение
    (как `tournament.js`/`broadcast.js`):** держи скрипт ГЛОБАЛЬНО (один раз в
    [App.razor](ChessSchool.Arena/Components/App.razor)); он сам инициализируется по появлению своего
    корневого узла (`MutationObserver` + `enhancedload` + первичная загрузка) и идемпотентен (teardown +
    generation-guard, разовая привязка слушателей к `document`); конфиг/локализацию страница отдаёт через
    `#root data-*` + `<script type="application/json">`, а не через инлайновый исполняемый скрипт.
    Альтернатива для редких страниц — пометить ссылки на неё `data-enhance-nav="false"` (полная загрузка).
    Это уточнение граблей #12: правило про `data-enhance-nav` там было сноской — на него и наступили.
    **Подвох наблюдателя (наступили на /pairings):** триггерить инициализацию по `addedNodes`-матчу корня
    НЕНАДЁЖНО — при enhanced-навигации Blazor часто МОРФИТ существующий узел (меняет атрибуты, не добавляет
    новый), и матч промахивается; `enhancedload` тоже может не сработать. Симптом: со списка/меню страница
    «не работает» (обработчики не привязаны), а по F5 — да. **Лечение:** наблюдатель реагирует на ЛЮБУЮ
    мутацию и зовёт `tryInit()`, который инициализирует, только если корень есть и ещё не помечен
    (`root.dataset.ready==='1'`) — ловит и морфинг, и не самозапускается на перерисовке потомков. Образец —
    [pairings.js](ChessSchool.Arena/wwwroot/js/pairings.js). Защищено e2e ([pairings.spec.js](e2e/tests/pairings.spec.js)).
14. **Цвет клеток JS-досок был инвертирован** (тонкие клиенты: `/play`, разбор партии, `broadcast.js`).
    Правильная окраска при индексации «file 0..7 (a..h), rank 1..8»: `dark = (file + rank) % 2 === 1`
    → **a1 тёмная, светлое поле справа-снизу (h1)**. Было `=== 0` → вся доска в инверсии («доску
    раскорячило» — позиции легальные, но клетки не того цвета). Серверная [Chessboard.razor](ChessSchool.Arena/Components/Chessboard.razor)
    использует ДРУГУЮ индексацию (`(row + col) % 2 == 1`, где `row=0` — 8-я горизонталь, `col=0` — `a`)
    и корректна — сверять JS-доски с ней. Защищено проверкой цвета углов в e2e ([broadcasts.spec.js](e2e/tests/broadcasts.spec.js)).
15. **CSS-сетка доски: задавать И строки, И столбцы.** Мини-доска трансляции (`.bd-mini`) имела только
    `grid-template-columns: repeat(8,1fr)` без `grid-template-rows` → при `aspect-ratio:1/1` ряды получали
    авто-высоту (ряды с фигурами выше пустых) → клетки разного размера («клетки не одинаковые»). Лечение:
    `grid-template-rows: repeat(8,1fr)` тоже + `min-width/height:0` на flex-ячейке. Оверлей и `/play` это
    задавали изначально. Защищено e2e-проверкой равенства размеров всех 64 клеток.
16. **Холодная сборка Blazor-проектов (Web/Arena) валится сотнями ложных Razor-ошибок на .NET SDK 10.0.300.**
    Симптом: `dotnet build` после удаления `obj/` (или сборка из Rider/CI на чистом дереве) выдаёт сотни
    ошибок во ВСЕХ `.razor` (в т.ч. нетронутых): `ParameterAttribute/EventCallback/NavigationManager не
    найдены`, `A compilation unit cannot directly contain members`, `__PrivateComponentRenderModeAttribute
    does not exist`, `<h1>@_name</h1>` парсится как C# (`_name<,>`, `h1` как типы). Причина — **баг Razor
    source-generator в SDK 10.0.300**: при полной генерации компонентов с `@rendermode` не эмитится партиал
    render-mode-атрибута, дальше каскад. Инкрементальная сборка 1–2 файлов и `dotnet test` проходят (берут
    кэш `.g.cs` в `obj/`), поэтому баг прячется до первой чистой сборки. Лечение (применено в
    [Web](ChessSchool.Web/ChessSchool.Web.csproj)/[Arena](ChessSchool.Arena/ChessSchool.Arena.csproj).csproj):
    `<UseRazorSourceGenerator>false</UseRazorSourceGenerator>` — классический компайл-тайм кодоген Razor
    (идентичный рантайм, **CSS-изоляция серверных компонентов сохраняется**, стабильная сборка). Убрать,
    когда SDK починят (или запинить рабочий 10.0.x в `global.json` — но другой 10.0.x должен быть установлен).
    Источник менять НЕ нужно — он валиден. Смена токенайзера (`_RazorUseRoslynTokenizer=false`) НЕ помогает —
    баг в самом SG. **Не диагностировать инкрементальной сборкой: проверять чистоту только через
    `--no-incremental` или удаление `obj/`.**
17. **Scoped `*.razor.css` НЕ достаёт страницы, построенные браузерным JS (тонкие клиенты).** Симптом:
    на `/t/{id}` доска «рассыпалась» в строку — грид `.board` не применялся (хотя CSS на месте). Причина:
    страница турнира целиком рисует `tournament.js` через `innerHTML` в `#t-root`; у этих DOM-узлов нет
    Blazor-scope-атрибута `b-xxx`, а scoped-стиль в бандле — `.board[b-xxx]`, поэтому к JS-узлам **не
    применяется в принципе** (без `::deep` — а он не помогает для узлов вне Blazor-дерева). Маскировалось
    старым кэшем бинарей; проявилось на чистой пересборке. **Правило: CSS для страниц-тонких-клиентов
    (`/play`, `/t/{id}` турнир, трансляции) держать ГЛОБАЛЬНЫМ** (`wwwroot/css/*.css` + `<link>` в App.razor,
    как [tournament.css](ChessSchool.Arena/wwwroot/css/tournament.css)), а НЕ в `*.razor.css`. Scoped
    `*.razor.css` — только для компонентов, чью разметку рендерит сам Blazor на сервере (Home, layout).
    Трансляции изначально верно держат CSS в инлайновом `<style>` (глобальный). Проверять визуально на
    реальной странице (Playwright), а не только сборкой.

## Безопасность и конфигурация (специфика)

- **Реестр всех политик и настроек безопасности — [docs/SECURITY.md](docs/SECURITY.md)** (что защищено, как
  настроено, компромиссы, что отложено). Обновляется в том же PR при изменении auth/сессий/секретов/CORS/токенов/лимитов
  — правило «реестр безопасности» в [CLAUDE.md](CLAUDE.md).
- **НИКОГДА не читать `.env`.** Нужна переменная — спросить её имя у пользователя. Шаблон — [env.example](env.example),
  держать актуальным. Не копировать секреты в коммиты/PR. (Общий принцип безопасности — в CLAUDE.md.)
- В репозиторий **не коммитятся**: `.env`, `*.db*`, `**/keys/*.pem` (см. [.gitignore](.gitignore)).
- Локально секреты подставляет AppHost (`InternalApiKey=dev-internal-key`). В проде — KMS/Key Vault, PostgreSQL, Redis.
