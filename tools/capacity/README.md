# Калькулятор ёмкости онлайн-игры

Оценка железа под целевое число одновременных игроков (путь GameServer: SignalR + Orleans-грейн на
партию). Воспроизводит модель из [../../docs/CAPACITY_PLANNING.md](../../docs/CAPACITY_PLANNING.md):
из удельных стоимостей и числа игроков считает число нод GameServer, нужду в Redis Cluster и темп
записей в Postgres.

## Запуск

```bash
# Оценка под 100k на дефолтных удельных стоимостях из плана:
dotnet run --project tools/capacity -- --players 100000

# То же, но удельные стоимости (ходы/с/ядро, память/партия) замерить на этой машине (Gera.Chess):
dotnet run -c Release --project tools/capacity -- --players 100000 --bench

# Другая цель / своё железо:
dotnet run --project tools/capacity -- --players 250000 --node-vcpu 16 --node-ram-gb 32 --conns-per-node 80000
```

Флаги (любой переопределяет дефолт): `--players`, `--bench`, `--moves-per-core`, `--bytes-per-game`,
`--conns-per-node`, `--node-vcpu`, `--node-ram-gb`, `--cpu-overhead`.

## Что измеримо, а что — модель

- **Измеримо локально** (`--bench`): пропускная способность обработки ходов на ядро и память на
  партию (на реальной шахматной библиотеке). Это floor — реальный путь грейна дешевле.
- **Модель** (нельзя снять с одной машины): плотность WebSocket-соединений на ноду, память на
  соединение, pub/с backplane. **Перед продом обязателен распределённый E2E-тест** на staging —
  k6-харнес [../loadtest](../loadtest), методика — [CAPACITY_PLANNING §6](../../docs/CAPACITY_PLANNING.md).

Математика модели покрыта тестами (`ChessSchool.Tests/CapacityModelTests`).
