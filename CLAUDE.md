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

## Главный принцип: всё рассчитано на несколько серверов

**Любое решение принимай исходя из того, что приложение работает на НЕСКОЛЬКИХ серверах
(несколько экземпляров/нод) одновременно.** Один процесс — это только частный случай. Это
правило приоритетно: если вариант работает на одной ноде, но ломается на нескольких — он
неправильный, даже если «локально работает».

Практические следствия (проверяй каждое изменение на их соответствие):

- **Никакого критичного состояния в памяти процесса.** Сессии/тикеты аутентификации, кэши,
  очереди, прогресс, счётчики, rate-limit, «кто онлайн» — во внешнем общем сторе
  (**PostgreSQL**/**Redis**), а не в полях синглтона или `IMemoryCache`. In-memory допустим только
  как локальный ускоритель поверх общего источника истины и обязан переживать потерю любой ноды.
- **Запрос может прийти на любую ноду.** Нет «липкости» к конкретному серверу: после рестарта/
  переезда запроса всё должно продолжать работать (пример — [FileSystemTicketStore](ChessSchool.WebAuth/SsoExtensions.cs):
  локально файл, в проде — Redis/общий стор).
- **Реалтайм масштабируется через backplane.** SignalR — общий backplane (**Redis**), Orleans —
  кластеризация; нельзя полагаться на то, что обе стороны диалога попали в один процесс.
- **Состояние партии — Orleans-грейн** (единственный владелец, однопоточный доступ), персист в
  общий стор; при реактивации на другой ноде состояние восстанавливается, а не теряется.
- **Никаких процесс-локальных блокировок/таймеров как источника истины.** `lock`, `static`,
  in-proc `Timer` координируют только в пределах ноды — для общей логики нужен распределённый
  механизм (грейн, БД-транзакция, Redis).
- **Идемпотентность и конкурентность.** Несколько нод могут обработать одно событие — операции
  делай идемпотентными, на общих данных рассчитывай на гонки (оптимистичный конкуренс-контроль/
  транзакции).
- **Конфиг/секреты/DataProtection-ключи — общие и стабильные между нодами и рестартами**
  (KMS/Key Vault, общий keyring), иначе cookie/токены, выданные одной нодой, не примет другая.

Локально (одна машина, Aspire) общий стор эмулируется (Postgres-контейнер, файл, in-proc), но
код и абстракции пишутся так, будто нод несколько. Если по-настоящему распределённый вариант
сейчас не делаем — оставляй интерфейс/точку расширения и пометку, что в проде это Redis/Postgres.

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
in-proc + **PostgreSQL** (контейнер от Aspire). Прод: Orleans clustering и SignalR backplane →
**Redis**, персист → тот же **PostgreSQL** (managed). dev и прод на одном провайдере БД —
схема через EF-миграции.

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
2. **БД — PostgreSQL для всех окружений** (SQLite убран). Схема версионируется EF-миграциями,
   на старте `db.Database.Migrate()` (ветка `IsNpgsql()`; для InMemory в тестах — `EnsureCreated()`).
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
   `Kestrel MaxRequestHeadersTotalSize=256KB`. Ticket-store **файловый** (`FileSystemTicketStore`,
   тикет шифруется DataProtection, папка `keys/auth-tickets`) — переживает перезапуск сервиса, иначе
   авторизованный пользователь после рестарта «выпадал» во «Вход» (in-memory терял тикеты). Прод —
   распределённый стор (Redis), общий для всех нод.
8. **Health-checks AppHost:** `WithHttpHealthCheck` по https с dev-сертификатом вешает `WaitFor`-каскад.
   Health маппится всегда (не только Development), интеграционный тест ждёт состояния `Running`, не `Healthy`.
9. **Arena-турнир переживает деактивацию грейна.** Мета+таблица персистятся в grain storage `"arena"`
   ([Program.cs](ChessSchool.Arena/Program.cs) `AddMemoryGrainStorage("arena")`, dev), грейн сам выводит
   мету из своего id через [ArenaSchedule](ChessSchool.Arena/Services/ArenaSchedule.cs) (`EnsureConfigured`),
   а пока турнир идёт держит себя живым (`DelayDeactivation`). Активные доски НЕ персистятся (при реактивации
   игроки переспариваются). Прод-долговечность через перезапуск силоса — заменить memory-storage на
   AdoNet(Postgres)/Redis. **Тестовый силос обязан тоже звать `AddMemoryGrainStorage("arena")`**, иначе
   `[PersistentState("tournament","arena")]` не резолвится.

## Безопасность и конфигурация

- **НИКОГДА не читать `.env`.** Нужна переменная — спросить её имя у пользователя. Шаблон — [env.example](env.example),
  держать актуальным. Не копировать секреты в коммиты/PR.
- В репозиторий **не коммитятся**: `.env`, `*.db*`, `**/keys/*.pem` (см. [.gitignore](.gitignore)).
- Локально секреты подставляет AppHost (`InternalApiKey=dev-internal-key`). В проде — KMS/Key Vault, PostgreSQL, Redis.

## Правило коммита

- **Закончил задачу — коммить сам, если всё работает.** «Работает» = собирается (`dotnet build`),
  зелёные тесты (`dotnet test`), чистый `dotnet format`. Изменения, не покрытые тестами, — добавить тесты.
  Сообщение коммита по-русски, осмысленное.
- **Если что-то не работает — НЕ коммить.** Вместо коммита показать пользователю, что именно сломано
  (ошибка сборки / упавший тест / лог) и что нужно доделать, чтобы заработало.
- **Push — по-прежнему только по явной просьбе.** Коммит ≠ push.
