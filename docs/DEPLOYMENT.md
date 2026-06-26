# Развёртывание ChessSchool в продакшене

Подробная инструкция по выкату всей платформы. Покрывает топологию, инфраструктуру, конфигурацию
каждого сервиса, безопасность, миграции, масштабирование Orleans/SignalR и чек-листы. Ориентир —
Kubernetes (или Azure Container Apps); шаги провайдеро-нейтральны там, где это возможно.

> **Главный принцип.** Приложение рассчитано на работу **на нескольких нодах** и на высокую нагрузку.
> В проде распределённые провайдеры (Redis/Postgres) **обязательны** — без них сервисы либо теряют
> мультисервер, либо данные. Подробнее — корневой `CLAUDE.md`.

---

## 1. Топология

| Компонент | Тип | Внешний доступ | Масштабирование | Зависимости |
|---|---|---|---|---|
| **Auth** (IdP, OpenIddict) | stateless web | **да** (публичный issuer) | горизонтально | Postgres `authdb`, Redis |
| **ApiService** (домен) | stateless web | **нет** (внутренний) | горизонтально | Postgres `schooldb`, Auth |
| **GameServer** (онлайн-игра) | Orleans-силос + SignalR | **да** (хаб для браузера) | как Orleans-кластер | Redis, Auth, ApiService |
| **Arena** (B2C-турниры) | Orleans-силос + Blazor SSR | **да** | как Orleans-кластер | Redis, Auth |
| **Web** (Blazor SSR) | stateless web | **да** | горизонтально | Auth, ApiService, GameServer, Redis |
| **PostgreSQL** | managed БД | нет | managed | — |
| **Redis** (TLS) | managed кэш/шина | нет | managed (cluster) | — |

**Потоки:** браузер → Web/Arena (SSR) и напрямую → GameServer `/gamehub` (SignalR). Все веб-приложения
аутентифицируются через Auth (OIDC, authorization code + PKCE). Server-to-server: ApiService→Auth
(резолв email→sub), GameServer→ApiService (архивация партий) — по заголовку `X-Internal-Key`.

> **AppHost — это только локальная оркестрация (dev).** В проде `ChessSchool.AppHost` **не запускается**:
> каждый сервис — отдельный контейнер/Deployment, а Postgres и Redis — **managed**-сервисы. AppHost
> полезен лишь как справочник связей ([AppHost.cs](../ChessSchool.AppHost/AppHost.cs)).

---

## 2. Предпосылки

- **Managed PostgreSQL** (одна СУБД, две базы: `authdb`, `schooldb`). Версия 14+.
- **Managed Redis с TLS** и паролем — один общий инстанс/кластер для: Orleans clustering, grain storage,
  reminders, SignalR backplane, DataProtection-keyring, ticket-store. Включить **persistence** (AOF/RDB) —
  в нём живёт состояние идущих турниров и членство кластера.
- **Container registry** (push образов).
- **Kubernetes** (или Azure Container Apps) с Ingress/LoadBalancer и TLS-терминацией.
- **Хранилище секретов**: Key Vault / AWS Secrets Manager / KMS (для `InternalApiKey`, паролей БД/Redis,
  сертификатов IdP).
- **Публичные DNS-имена** для Auth, Web, Arena, GameServer + валидные TLS-сертификаты.

---

## 3. Сборка образов

.NET 10 SDK умеет собирать контейнеры без Dockerfile. Для каждого сервиса:

```bash
dotnet publish ChessSchool.Auth       -c Release --os linux --arch x64 /t:PublishContainer \
  -p:ContainerRegistry=registry.example.com -p:ContainerRepository=chessschool/auth       -p:ContainerImageTag=$VERSION
dotnet publish ChessSchool.ApiService -c Release --os linux --arch x64 /t:PublishContainer \
  -p:ContainerRegistry=registry.example.com -p:ContainerRepository=chessschool/apiservice -p:ContainerImageTag=$VERSION
dotnet publish ChessSchool.GameServer -c Release --os linux --arch x64 /t:PublishContainer \
  -p:ContainerRegistry=registry.example.com -p:ContainerRepository=chessschool/gameserver -p:ContainerImageTag=$VERSION
dotnet publish ChessSchool.Arena      -c Release --os linux --arch x64 /t:PublishContainer \
  -p:ContainerRegistry=registry.example.com -p:ContainerRepository=chessschool/arena      -p:ContainerImageTag=$VERSION
dotnet publish ChessSchool.Web        -c Release --os linux --arch x64 /t:PublishContainer \
  -p:ContainerRegistry=registry.example.com -p:ContainerRepository=chessschool/web        -p:ContainerImageTag=$VERSION
```

> Перед сборкой: `dotnet test` (зелёный), `dotnet format --verify-no-changes` (чисто). Тегируйте образы
> неизменяемой версией (git sha), не `latest`.

Альтернатива: `aspirate` (Aspir8) — генерирует k8s-манифесты из AppHost; или `azd up` для Azure
Container Apps. Ниже — ручной k8s-подход (он переносим).

---

## 4. Конфигурация (переменные окружения)

ASP.NET читает вложенные ключи через `__`. Общее для **всех** сервисов:

```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true     # за TLS-терминирующим прокси (см. §6)
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317   # телеметрия (опц., но желательно)
```

### Service discovery без Aspire
Код находит другие сервисы по схеме `https+http://<имя>`, резолвя её из ключей `services:<имя>:https:0`.
В проде задайте эти ключи env-переменными (cluster-internal DNS либо публичный URL — см. нюанс по issuer):

```
services__auth__https__0=https://auth.example.com          # ПУБЛИЧНЫЙ (issuer токенов)
services__apiservice__https__0=http://apiservice.prod.svc:8080   # внутренний
services__gameserver__https__0=https://game.example.com     # публичный (для ссылок/хаба)
```

> **Нюанс issuer.** Токены выпускает Auth по публичному URL → `iss` = публичный. GameServer валидирует
> `iss`, поэтому его `services__auth__https__0` должен указывать на **тот же публичный** Auth-URL
> (иначе `invalid_token`). Для не-OIDC вызовов (ApiService→Auth, GameServer→ApiService) можно
> использовать внутренний DNS.

### Per-service

**Auth** ([Program.cs](../ChessSchool.Auth/Program.cs), [SsoExtensions](../ChessSchool.WebAuth/SsoExtensions.cs) не используется здесь):
```
ConnectionStrings__authdb=Host=...;Database=authdb;Username=...;Password=<secret>
ConnectionStrings__redis=<host>:6380,password=<secret>,ssl=true
InternalApiKey=<secret>                       # общий с ApiService/GameServer
Sso__Clients__chessschool-web=https://app.example.com    # базовый URL веб-клиента (redirect_uri)
Sso__Clients__arena-web=https://arena.example.com        # базовый URL арены
# Сертификаты IdP — см. §5 (signing/encryption)
```

**ApiService** ([Program.cs](../ChessSchool.ApiService/Program.cs)):
```
ConnectionStrings__schooldb=Host=...;Database=schooldb;Username=...;Password=<secret>
InternalApiKey=<secret>
services__auth__https__0=https://auth.example.com
```

**GameServer** ([Program.cs](../ChessSchool.GameServer/Program.cs)):
```
ConnectionStrings__redis=<host>:6380,password=<secret>,ssl=true
InternalApiKey=<secret>
services__auth__https__0=https://auth.example.com
services__apiservice__https__0=http://apiservice.prod.svc:8080
Jwt__Audience=chessschool-api
Cors__Origins__0=https://app.example.com      # публичные origin'ы фронта (Web + Arena)
Cors__Origins__1=https://arena.example.com
```

**Arena** ([Program.cs](../ChessSchool.Arena/Program.cs)):
```
ConnectionStrings__redis=<host>:6380,password=<secret>,ssl=true
services__auth__https__0=https://auth.example.com
Sso__ClientId=arena-web
```

**Web** ([Program.cs](../ChessSchool.Web/Program.cs)):
```
ConnectionStrings__redis=<host>:6380,password=<secret>,ssl=true
services__auth__https__0=https://auth.example.com
services__apiservice__https__0=http://apiservice.prod.svc:8080
services__gameserver__https__0=https://game.example.com
Sso__ClientId=chessschool-web
```

> **Строка подключения Redis должна включать `ssl=true`** — Aspire-Redis и managed-Redis работают по TLS.
> Наличие `ConnectionStrings__redis` автоматически включает распределённые провайдеры
> ([GetRedisConnectionString](../ChessSchool.ServiceDefaults/Extensions.cs)); его отсутствие в проде —
> ошибка (см. чек-лист §12).

---

## 5. Безопасность и секреты

Все секреты — из хранилища секретов (Key Vault/KMS), смонтированные как env/файлы. **Никогда** в образ,
git или логи.

1. **`InternalApiKey`** — единый сильный секрет (32+ байт случайных). Одинаковый в Auth, ApiService,
   GameServer. Вне Development пустой/дефолтный ключ роняет старт
   ([ResolveInternalApiKey](../ChessSchool.ServiceDefaults/Extensions.cs)).
2. **Пароли БД и Redis** — из секретов; у Postgres-пользователей минимум прав (отдельные роли на `authdb`
   и `schooldb`).
3. **Сертификаты IdP (критично).** Сейчас OpenIddict использует **dev-сертификаты**
   (`AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate()`,
   [Auth/Program.cs](../ChessSchool.Auth/Program.cs)). Для прода замените на постоянные X.509:
   ```csharp
   o.AddSigningCertificate(signingCert)       // напр. из файла .pfx/секрета
    .AddEncryptionCertificate(encryptionCert);
   ```
   Иначе при перезапуске/масштабировании ключи подписи разъезжаются → выданные токены/JWKS перестают
   валидироваться. Сертификаты храните в секрете и монтируйте; ротация — по двум активным ключам.
   `DisableAccessTokenEncryption()` оставлен сознательно (JWT читается resource-серверами).
4. **CORS GameServer** — строгий список публичных origin'ов фронта (`Cors__Origins__*`); вне Development
   пустой список роняет старт. any-origin + credentials в проде = дыра.
5. **HTTPS везде**, `RequireHttpsMetadata` вне Development = true (уже зашито). Внутренний эндпоинт
   `/internal/games/archive` доступен только по `X-Internal-Key` — дополнительно закройте сетевой
   политикой (NetworkPolicy: только из GameServer).

---

## 6. Reverse proxy / forwarded headers / issuer

За Ingress с TLS-терминацией приложение видит запрос как http — это ломает построение issuer/redirect_uri
и HTTPS-проверки. Включите обработку forwarded-заголовков:

- Установите `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (включает `UseForwardedHeaders` для `X-Forwarded-Proto`/`-For`).
- Ingress должен прокидывать `X-Forwarded-Proto: https` и `X-Forwarded-Host`.
- Для **Auth** issuer в `/.well-known/openid-configuration` и `iss` токенов строится из публичного URL —
  он должен совпадать с `services__auth__https__0` у клиентов и с фактическим внешним адресом.
- **redirect_uri** клиентов сидятся из `Sso__Clients__<id>` ([ClientSeeder](../ChessSchool.Auth/ClientSeeder.cs)):
  `<baseUrl>/signin-oidc` и `<baseUrl>/signout-callback-oidc`. Значения **обязаны** совпадать с реальными
  публичными URL Web/Arena, иначе OIDC вернёт `invalid_request` (та же грабля, что с портами в dev).

WebSocket для SignalR: Ingress должен разрешать `Upgrade`/`Connection` и иметь увеличенные таймауты
(хаб держит долгие соединения). Токен SignalR приходит в query string `access_token` — не логируйте
query на прокси.

---

## 7. База данных и миграции

- Схема версионируется **EF-миграциями**; провайдер — **PostgreSQL** для всех окружений.
- Миграции выкатываются **отдельным шагом** — тем же образом, запущенным с аргументом `migrate`:
  он применяет схему и завершается (для k8s `Job`/init-контейнера перед раскаткой реплик). Боевые
  реплики стартуют **без** авто-миграции (нет гонки реплик за первую миграцию).
  ```bash
  # k8s Job (один раз перед обновлением реплик), образ тот же:
  #   command: ["dotnet","ChessSchool.Auth.dll","migrate"]
  #   command: ["dotnet","ChessSchool.ApiService.dll","migrate"]
  ```
- Поведение управляется флагом `Database:MigrateAtStartup` (по умолчанию = `IsDevelopment()`): в
  Development реплики мигрируют сами (удобно локально), в Production — нет (мигрирует только Job).
  При необходимости можно временно включить авто-миграцию на старте: `Database__MigrateAtStartup=true`.
- `EnsureCreated()` используется только для InMemory в тестах — в проде не задействован.
- **Seed-данные** (`SeedData.Ensure`, демо-школа `Demo.SchoolId`) — для прода уберите/огородите флагом,
  чтобы не плодить демо-сущности в боевой БД.

---

## 8. Redis

Один общий Redis обслуживает (ключевые префиксы видны в проде):
- `chessschool-arena/members/*`, `chessschool-game/members/*` — Orleans clustering (членство силосов);
- `chessschool-arena/state/arenatournament/*` — grain storage турниров;
- `chessschool-arena/reminders` — reminders (воскрешение турнира при потере ноды);
- `chessschool:dataprotection-keys` — общий DataProtection-keyring;
- `chessschool:tickets:*` — distributed ticket-store (серверная auth-сессия);
- pub/sub-канал `arena:notify` и каналы SignalR backplane (без персиста).

Требования: **TLS**, пароль, persistence включён, HA (Sentinel/Cluster) для прода. Память: состояние
идущих турниров + keyring + тикеты + членство — обычно немного; основная нагрузка — pub/sub и backplane.
DataProtection-ключи общие → **rolling-рестарты не разлогинивают** пользователей.

---

## 9. Orleans (GameServer и Arena)

- **Кластеризация — через Redis** (`UseRedisClustering`), переключается наличием `redis`
  ([Arena](../ChessSchool.Arena/Program.cs)/[GameServer](../ChessSchool.GameServer/Program.cs)).
- **Два независимых кластера**, изолированы по `clusterId`: `chessschool-game` и `chessschool-arena`.
  Деплоить **раздельными** Deployment'ами — нельзя смешивать силосы разных кластеров в одном поде.
- **Порты силоса/гейтвея зашиты**: GameServer `11111/30000`, Arena `11112/30001`. В k8s каждый под имеет
  свой IP, поэтому фикс. порты не конфликтуют **между подами**; но в одном поде второй силос не поднять.
- **Advertised IP**: `ConfigureEndpoints(siloPort, gatewayPort)` должен анонсировать **IP пода**, а не
  loopback. Прокиньте `POD_IP` (k8s downward API) и убедитесь, что силосы видят друг друга:
  ```yaml
  env:
    - name: POD_IP
      valueFrom: { fieldRef: { fieldPath: status.podIP } }
  ```
  Силос-к-силосу трафик (порты 11111/11112 и 30000/30001) разрешите между подами **headless Service** +
  NetworkPolicy. Эти порты **не** публикуются наружу.
- **Reminders** (`UseRedisReminderService`) включаются вместе с Redis; турнирный грейн (`IRemindable`)
  переживает потерю ноды — воскресает на другой и восстанавливает состояние из grain storage.
- **Масштабирование**: добавляйте реплики GameServer/Arena — Orleans балансирует грейны по кластеру.
  Грейн партии/турнира — единственная активация на весь кластер (нет гонок). При scale-down Orleans
  переносит грейны; активные доски не персистятся (игроки переспариваются), мета/таблица турнира — в Redis.

---

## 10. Сеть и Ingress

| Хост | Сервис | Назначение |
|---|---|---|
| `auth.example.com` | Auth | публичный IdP (OIDC/JWKS) |
| `app.example.com` | Web | основное веб-приложение |
| `arena.example.com` | Arena | B2C-турниры |
| `game.example.com` | GameServer | SignalR-хаб `/gamehub` (WebSocket) |
| — (internal) | ApiService | только из кластера |

- **CORS** GameServer = `https://app.example.com`, `https://arena.example.com`.
- **Sticky sessions не нужны** для SignalR (есть Redis backplane), но WebSocket-апгрейд и длинные таймауты
  на Ingress настроить обязательно.
- ApiService **не** выставляйте наружу; доступ — только Web/GameServer внутри кластера (+ `X-Internal-Key`).

---

## 11. Здоровье, телеметрия, пробы

- Эндпоинты: `/alive` (liveness, тег `live`) и `/health` (readiness) —
  [ServiceDefaults](../ChessSchool.ServiceDefaults/Extensions.cs). Маппятся всегда; **закройте их от
  публичного доступа** на Ingress/NetworkPolicy.
  ```yaml
  livenessProbe:  { httpGet: { path: /alive,  port: 8080 } }
  readinessProbe: { httpGet: { path: /health, port: 8080 } }
  ```
- Телеметрия (логи/метрики/трейсы) — OpenTelemetry → коллектор через `OTEL_EXPORTER_OTLP_ENDPOINT`.
- Рекомендуется добавить health-checks на зависимости (Postgres/Redis) перед боевым запуском (сейчас
  health = базовый `self`).

---

## 12. Pre-flight чек-лист (вне Development всё обязательно)

- [ ] `ASPNETCORE_ENVIRONMENT=Production` на всех сервисах.
- [ ] `InternalApiKey` — реальный секрет (не `dev-internal-key`), одинаковый в Auth/ApiService/GameServer.
- [ ] `ConnectionStrings__redis` задан везде, где нужен (`ssl=true`); Redis — TLS, пароль, persistence, HA.
- [ ] `ConnectionStrings__authdb` / `__schooldb` заданы; миграции применены отдельным шагом.
- [ ] `Cors__Origins__*` у GameServer = публичные origin'ы Web и Arena.
- [ ] `Sso__Clients__chessschool-web` / `__arena-web` = публичные URL (redirect_uri совпадут).
- [ ] `services__auth__https__0` = публичный Auth-URL (issuer) у всех клиентов и GameServer.
- [ ] IdP: **постоянные** signing/encryption-сертификаты вместо dev-сертификатов.
- [ ] Forwarded headers включены; Ingress прокидывает `X-Forwarded-Proto: https`.
- [ ] `POD_IP` прокинут в GameServer/Arena; силос-порты доступны между подами, закрыты наружу.
- [ ] Seed/демо-данные отключены в боевой БД.
- [ ] `/health`, `/alive`, ApiService и Orleans-порты недоступны из интернета.

> Сильная рекомендация: дополнительно сделать выбор провайдера **fail-fast** — вне Development падать,
> если `redis` не задан (а не тихо скатываться в in-memory). См. обсуждение в истории/`CLAUDE.md`.

---

## 13. Порядок выката (нулевой простой)

1. **Postgres** и **Redis** (managed) подняты, доступны, секреты в хранилище.
2. **Миграции** — job'ом на том же образе: `dotnet ChessSchool.Auth.dll migrate` и
   `dotnet ChessSchool.ApiService.dll migrate` (см. §7).
3. **Auth** → дождаться `/health` Ready (ClientSeeder создаст/обновит OIDC-клиентов из `Sso__Clients`).
4. **ApiService** → Ready.
5. **GameServer** и **Arena** (Orleans-силосы) → Ready (формируют кластеры в Redis).
6. **Web** → Ready.
7. Rolling-update последующих версий: общий DataProtection-keyring и распределённое состояние позволяют
   перезапускать реплики без разлогина и без потери идущих турниров.

---

## 14. Дымовые тесты после выката

```bash
# IdP жив, discovery отдаёт публичный issuer
curl -s https://auth.example.com/.well-known/openid-configuration | jq .issuer
curl -s https://auth.example.com/.well-known/jwks | jq '.keys | length'   # только публичные n,e

# Хаб требует токен (ожидаем 401, не 500)
curl -s -o /dev/null -w '%{http_code}\n' -X POST https://game.example.com/gamehub/negotiate

# Веб-приложения отвечают
curl -s -o /dev/null -w '%{http_code}\n' https://app.example.com/
curl -s -o /dev/null -w '%{http_code}\n' https://arena.example.com/

# В Redis появились членство силосов, grain storage, reminders, keyring (через TLS, с паролем):
#   chessschool-arena/members/*, chessschool-game/members/*,
#   chessschool-arena/state/*, chessschool-arena/reminders, chessschool:dataprotection-keys
```

Затем — ручной прогон: логин через Auth → открытие профиля (Web) → вход в турнир (Arena) → онлайн-партия
(GameServer). Проверить, что после перезапуска одной реплики пользователь остаётся залогинен, а идущий
турнир продолжается.

---

## 15. Что доработать для полной прод-зрелости (известный долг)

- Вынести **миграции** из старта в отдельный шаг (см. §7).
- Заменить **dev-сертификаты IdP** на постоянные (см. §5) — без этого мультисервер-IdP некорректен.
- Сделать выбор провайдеров **fail-fast** вне Development (см. §12).
- Добавить **health-checks на Postgres/Redis** и (опц.) интеграционный тест на распределённые пути.
- Параметризовать Orleans-порты конфигом, если потребуется нестандартная схема подов.

См. также корневой `CLAUDE.md` (принципы и грабли) и [docs/TODO.md](TODO.md).
