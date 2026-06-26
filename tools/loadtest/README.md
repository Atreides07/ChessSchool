# Нагрузочный тест GameServer (`/gamehub`)

Полный E2E-харнес для пути онлайн-игры: k6 поднимает тысячи WebSocket-клиентов, каждый проходит
матчмейкинг и играет легальную партию, измеряя задержку хода. Модель ёмкости и требования к железу —
[../../docs/CAPACITY_PLANNING.md](../../docs/CAPACITY_PLANNING.md).

## Состав
- `gamehub-loadtest.js` — k6-сценарий: SignalR-handshake → `FindMatch` → стейт-машина партии (ход
  выбирается по `turn`), метрики `move_rtt`, `match_wait`, `moves_sent`, `errors`. Реализм:
  **распределение контролей времени** (bullet/blitz/rapid, веса в `TC_MIX`) с темпом хода под контроль,
  и **несколько дебютов** — дебют выбирается детерминированно по `gameId`, поэтому оба игрока партии
  играют одну легальную линию без рассинхрона.
- `get-tokens.mjs` — харвестер access-токенов (OIDC authorization code + PKCE) для пула тест-юзеров.

## Предусловия
- **k6** (`brew install k6` / пакет дистрибутива).
- **Node 18+** (для харвестера токенов).
- **Изолированный staging-кластер**, идентичный проду (N нод GameServer, Redis Cluster, Postgres).
  100k/1M с одной машины не снять — нужны несколько load-нод или **k6 Cloud** (`k6 cloud run ...`).

## Шаг 1. Токены
`/gamehub` под `[Authorize]` (JWT от IdP, audience `chessschool-api`, scope `chess.api`). Наберите пул:

```bash
NODE_TLS_REJECT_UNAUTHORIZED=0 \  # только для self-signed staging
IDP=https://auth.staging.example.com \
REDIRECT=https://app.staging.example.com/signin-oidc \  # = seeded redirect_uri клиента
CLIENT_ID=chessschool-web COUNT=500 OUT=./tokens.json \
node get-tokens.mjs
```
Получите `tokens.json` (массив строк). Токенов нужно ≥ числу одновременных VU (иначе переиспользуются —
для матчмейкинга лучше уникальные, чтобы игроки не «самоспаривались»).

## Шаг 2. Прогон
```bash
k6 run \
  -e HUB=wss://game.staging.example.com \   # без /gamehub
  -e TOKENS=./tokens.json \
  -e VUS=2000 -e RAMP=3m -e HOLD=10m \
  -e THINK_SCALE=1 \                         # множитель темпа хода (контроли времени — в TC_MIX скрипта)
  gamehub-loadtest.js
```
Масштабирование: запускайте с нескольких load-нод (каждая свой диапазон токенов) или `k6 cloud`.
Нарастите `VUS` до нарушения порогов (`thresholds` в скрипте: p95 move_rtt < 250 мс) — это ёмкость стенда.

## Что смотреть (привязка к §6 CAPACITY_PLANNING)
- **k6:** `move_rtt` (p50/p95/p99), `match_wait`, `errors`, throughput `moves_sent`.
- **Сервер (через дашборд/Seq/OTel):** CPU/RAM на ноду GameServer, плотность WS/ноду до деградации, GC.
- **Redis:** pub/sec backplane, CPU, задержка.
- **Postgres:** записи/с (архивация), задержка, лаг реплик.

## Заметки
- `insecureSkipTLSVerify` в скрипте включён для staging с self-signed — **в боевой проверке уберите**.
- Контроли времени и их доли настраиваются в `TC_MIX` (по умолчанию bullet 30% / blitz 50% / rapid 20%);
  набор дебютов — в `OPENINGS`. Это «честная» нагрузка по матчмейкингу и кадансу ходов.
- Харвестер — для **изолированного** контура; не запускать против боевого IdP с реальными пользователями.
- Локально под Aspire порт/URL GameServer динамические — берите внешний адрес из дашборда; для смоука
  удобнее staging с фиксированными хостами.
