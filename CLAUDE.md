# CLAUDE.md

Гайд для работы с этим репозиторием. Отвечать пользователю **на русском языке**.

## Что это

**ChessSchool** — платформа для шахматных школ на базе idchess: учёт партий и рейтинга учеников,
страница прогресса для родителей по ссылке, и **онлайн-игра в реальном времени** с прицелом на
масштаб (целевой ориентир — до 1 млн одновременных партий). Подробное ТЗ:
[docs/TЗ_ChessSchool_Platform.md](docs/T%D0%97_ChessSchool_Platform.md).

Стек: **.NET 10**, **Blazor Web App (SSR ради SEO)**, оркестрация — **.NET Aspire** (локально без
Docker). Онлайн-игра — **Microsoft Orleans** (грейн на партию) + **SignalR**. Авторизация — отдельный
переиспользуемый **IdP на OpenIddict 7.5** (OIDC/JWT/JWKS), как Google Auth (явное требование заказчика).

Известный технический долг и отложенные задачи — [docs/TODO.md](docs/TODO.md).

## Команды

```bash
dotnet run --project ChessSchool.AppHost   # запуск всего (откроется дашборд Aspire). Docker НЕ нужен.
dotnet test                                # юнит + интеграционные (вкл. полный старт AppHost; ~до 180с)
dotnet format                              # анализатор стиля/кода
dotnet build                               # сборка решения
```

Точка входа после запуска — `webfrontend` (бери внешний URL **из дашборда Aspire**, не Kestrel-порт —
см. гочу про redirect_uri ниже). Маршруты: `/` лендинг, `/school` ЛК школы, `/students/{id}` профиль,
`/p/{token}` публичный профиль родителю, `/attribution` очередь атрибуции, `/play` онлайн-партия.

После изменений в коде/конфиге **всегда** запускай `dotnet test` и `dotnet format`. Если изменения
не покрыты тестами — добавь тесты.

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
(Orleans-силосы) масштабируются независимо. Локально: Orleans `localhost`-кластер + SignalR
in-proc + SQLite. Прод: Orleans clustering и SignalR backplane → **Redis**, персист → **PostgreSQL**.
Код не меняется — провайдер БД переключается `UsePostgres=true` в AppHost.

## Архитектурные решения

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

1. **Razor: строковый параметр компонента биндить ВСЕГДА через `@`.** `Fen="g.Fen"` передаёт литерал
   `"g.Fen"`, а не значение (для `bool` без `@` выражение вычисляется — отсюда коварство). Правильно:
   `Fen="@g.Fen"`.
2. **SQLite не поддерживает ORDER BY/сравнение по `DateTimeOffset`** (рантайм `NotSupportedException`).
   В обоих DbContext стоит `DateTimeOffsetToBinaryConverter` через `ConfigureConventions` — **не убирать**.
3. **Два Orleans-силоса (GameServer и Arena) на одной машине конфликтуют** портами/clusterId.
   Разведены: GameServer `11111/30000 clusterId=chessschool-game`, Arena `11112/30001 clusterId=chessschool-arena`.
4. **IdP: JWK строить только из публичных параметров** (`n,e`) — `ConvertFromRSASecurityKey` на ключе
   с приватной частью утекает `d,p,q,...` в JWKS. Защищено `JwksSecurityTests`.
5. **`EnsureCreated()` не мигрирует существующую БД.** После добавления таблиц (напр. OpenIddict) старый
   `*.db` падает с `no such table`. Лечение в dev: удалить `**/Data/*.db*` (пересоздастся). В проде — миграции EF.
6. **redirect_uri / порты под Aspire:** внешний порт веба ≠ Kestrel-порт. Заходить по **внешнему URL из
   дашборда** (совпадает с seeded redirect_uri). Иначе OIDC строит redirect_uri с Kestrel-портом → `invalid_request`.
7. **HTTP 431 на всех localhost-страницах = раздутая auth-cookie.** Фикс в
   [SsoExtensions](ChessSchool.WebAuth/SsoExtensions.cs): server-side ticket-store (в cookie только ключ) +
   `Kestrel MaxRequestHeadersTotalSize=256KB`.
8. **Health-checks AppHost:** `WithHttpHealthCheck` по https с dev-сертификатом вешает `WaitFor`-каскад.
   Health маппится всегда (не только Development), интеграционный тест ждёт состояния `Running`, не `Healthy`.

## Безопасность и конфигурация

- **НИКОГДА не читать `.env`.** Нужна переменная — спросить её имя у пользователя. Шаблон — [env.example](env.example),
  держать актуальным. Не копировать секреты в коммиты/PR.
- В репозиторий **не коммитятся**: `.env`, `*.db*`, `**/keys/*.pem` (см. [.gitignore](.gitignore)).
- Локально секреты подставляет AppHost (`InternalApiKey=dev-internal-key`). В проде — KMS/Key Vault, PostgreSQL, Redis.
- Коммитить/пушить только по явной просьбе пользователя.
