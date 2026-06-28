# Демо через dev tunnels: логин + игра + шаринг тестировщикам

Как поднять локальный стенд за публичными адресами (Microsoft **dev tunnels**), чтобы тестировщики
заходили с любого устройства, логинились через общий IdP и играли. Обычный dev/прод этим не
затрагиваются — публичные адреса включаются конфигом `DemoTunnels:*` у AppHost (если он пуст,
поведение прежнее).

> Это запускается на твоей машине: пока AppHost и туннели подняты — демо доступно; выключил — недоступно.
> Для постоянного стенда — обычный деплой (см. [DEPLOYMENT.md](DEPLOYMENT.md)).

## Почему нужно 4 туннеля

Сервисы на разных хостах, и часть адресов **браузер‑facing** (их видит устройство тестировщика):
- **auth** — IdP: туда редиректит браузер на логин; его же URL становится `issuer` токена.
- **webfrontend** и **arena** — сами приложения (их `redirect_uri` регистрируется в IdP).
- **gameserver** — хаб `/play` (браузерный SignalR подключается напрямую).

Внутренние вызовы (web→apiservice и т.п.) остаются внутри Aspire — их не туннелим.

Ключевое: все потребители должны использовать **публичный auth‑URL как authority** — тогда и редирект
браузера ведёт на доступный адрес, и `issuer` токена совпадает с тем, что проверяет gameserver
(иначе 401 — см. [CAPACITY_PLANNING §1.1](CAPACITY_PLANNING.md)). IdP строит issuer/эндпоинты из
`X‑Forwarded‑*` ([Auth/Program.cs](../ChessSchool.Auth/Program.cs)), forwarded‑заголовкам доверяем
([ServiceDefaults](../ChessSchool.ServiceDefaults/Extensions.cs)) — поэтому за туннелем всё сходится.

## Предусловия

```bash
# devtunnel CLI (macOS)
brew install --cask devtunnel   # или: https://aka.ms/devtunnel/install
devtunnel user login            # вход в аккаунт Microsoft/GitHub
```

## Шаг 1. Стабильные порты (один раз)

URL туннеля привязан к локальному порту, поэтому порты 4 сервисов нужно зафиксировать (иначе при
каждом запуске Aspire порт — и публичный URL — меняются). Пропиши фиксированные https‑порты в
`launchSettings.json` каждого проекта (профиль, который запускает AppHost), например:

| Сервис | Порт (пример) |
|---|---|
| auth | 7139 |
| webfrontend | 7100 |
| arena | 7200 |
| gameserver | 7300 |

(значения свои; главное — постоянные между запусками)

## Шаг 2. Постоянные туннели (один раз)

```bash
devtunnel create chess-demo                  # один тунель-контейнер
devtunnel port create chess-demo -p 7139     # auth
devtunnel port create chess-demo -p 7100     # web
devtunnel port create chess-demo -p 7200     # arena
devtunnel port create chess-demo -p 7300     # gameserver
devtunnel show chess-demo                     # покажет публичные URL вида https://<id>-7139.<region>.devtunnels.ms
```

Запиши 4 полученных URL — это `AUTH_URL`, `WEB_URL`, `ARENA_URL`, `GAME_URL`.

## Шаг 3. Включить демо‑режим у AppHost

Передай публичные URL в AppHost (user‑secrets — без коммита в репозиторий):

```bash
cd ChessSchool.AppHost
dotnet user-secrets set "DemoTunnels:Auth"       "$AUTH_URL"
dotnet user-secrets set "DemoTunnels:Web"        "$WEB_URL"
dotnet user-secrets set "DemoTunnels:Arena"      "$ARENA_URL"
dotnet user-secrets set "DemoTunnels:GameServer" "$GAME_URL"
```

В этом режиме AppHost ([AppHost.cs](../ChessSchool.AppHost/AppHost.cs)) проставит сервисам:
`Sso__Clients__*` (redirect_uri = публичные адреса), `Sso__Authority` (web/arena/gameserver = `AUTH_URL`),
`GameServer__PublicUrl` (= `GAME_URL`). Сидер IdP обновит redirect_uri на старте
([ClientSeeder.cs](../ChessSchool.Auth/ClientSeeder.cs)).

> Очистить демо‑режим: `dotnet user-secrets remove "DemoTunnels:Auth"` (и остальные) — вернётся обычный dev.

## Шаг 4. Запуск

```bash
dotnet run --project ChessSchool.AppHost     # дождись, пока все сервисы поднимутся
devtunnel host chess-demo                     # в отдельном терминале — публикует все 4 порта
```

## Шаг 5. Дай тестировщикам

- Лендинг/школа: `WEB_URL`
- Арена‑турниры: `ARENA_URL`
- Онлайн‑партия: `WEB_URL/play`

При первом заходе dev tunnels показывают одноразовую страницу‑предупреждение — нажать «Continue».

## Гочи

- **CORS gameserver** в Development = любой origin (грабля #5), поэтому туннельный origin примет браузерный
  SignalR. В проде так нельзя — там строгий `Cors:Origins`.
- **Cross‑site cookie OIDC**: app и auth — разные туннельные хосты → корреляционная cookie OIDC
  (`SameSite=None; Secure`) переживает редирект только по https. Туннель — https, поэтому ок.
- **Только публичные/SSR‑страницы без логина** работают и при одном туннеле; полноценный логин/игра
  требуют всех 4 (этот рунбук).
- **`/play`** требует туннеля gameserver (`GAME_URL`); **арена** играет через `/arenahub` (same‑origin
  arena), отдельного туннеля под хаб не нужно — хватает `ARENA_URL`.
- **Секреты не коммитим**: публичные URL — в user‑secrets/env, не в репозитории.

## Что в коде поддерживает это

- [ResolveSsoAuthority](../ChessSchool.ServiceDefaults/Extensions.cs) — authority из `Sso:Authority`
  (override) либо service discovery. Используется в [SsoExtensions](../ChessSchool.WebAuth/SsoExtensions.cs)
  (web/arena OIDC) и [GameServer](../ChessSchool.GameServer/Program.cs) (JWT).
- [Play.razor](../ChessSchool.Web/Components/Pages/Play.razor) — хаб из `GameServer:PublicUrl` (override)
  либо service discovery.
- [AppHost.cs](../ChessSchool.AppHost/AppHost.cs) — демо‑режим по `DemoTunnels:*` (иначе поведение прежнее).
