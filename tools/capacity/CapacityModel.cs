namespace ChessSchool.Capacity;

/// <summary>
/// Входные параметры модели ёмкости онлайн-игры (путь GameServer: SignalR + Orleans-грейн на партию).
/// Дефолты — из docs/CAPACITY_PLANNING.md. Удельные стоимости хода/памяти можно замерить на машине
/// (см. <see cref="Bench"/>); транспорт/плотность соединений — модельные (валидировать на staging).
/// </summary>
public sealed record CapacityInputs
{
    public int Players { get; init; } = 100_000;
    public double MovesPerGamePerSec { get; init; } = 0.2;   // темп: ~ход раз в 5 c (пик активной фазы)
    public int AvgGameDurationSec { get; init; } = 300;      // средняя партия (для темпа завершений)
    public double MovesPerSecPerCore { get; init; } = 6080;  // обработка ходов на ядро (измеримо)
    public double CpuOverheadFactor { get; init; } = 3.0;    // транспорт/сериализация ≥ шахматной логики
    public int BytesPerActiveGame { get; init; } = 20_000;   // доска+история+грейн (измеримо)
    public int BytesPerConnection { get; init; } = 30_000;   // буферы+контекст SignalR (модель)
    public int ConnectionsPerNode { get; init; } = 50_000;   // плотность WS/ноду (модель — валидировать!)
    public int NodeVCpu { get; init; } = 8;
    public double NodeRamGb { get; init; } = 16;
    public double NodeRamUsableFraction { get; init; } = 0.75; // запас на рантайм/GC
    public int RedisPubPerSecPerInstance { get; init; } = 80_000; // выше → нужен Redis Cluster
    public double FailoverHeadroom { get; init; } = 0.3;     // +запас нод на отказ/пики
}

/// <summary>Результат оценки: производные величины нагрузки + размер ярусов.</summary>
public sealed record CapacitySizing(
    int Players,
    int ActiveGames,
    double MovesPerSec,
    double FinishesPerSec,
    double ChessCores,
    double EffectiveCores,
    double StateMemGb,
    double ConnMemGb,
    int NodesByConnections,
    int NodesByCpu,
    int NodesByMemory,
    int GameServerNodes,
    double RedisPubPerSec,
    bool RedisClusterNeeded,
    double PostgresWritesPerSec);

/// <summary>
/// Чистая модель ёмкости (детерминированная, тестируемая). Воспроизводит §2–§5 CAPACITY_PLANNING:
/// из удельных стоимостей и целевого числа игроков считает число нод GameServer (по соединениям/CPU/
/// памяти, +запас), нужду в Redis Cluster и темп записей в Postgres.
/// </summary>
public static class CapacityModel
{
    public static CapacitySizing Estimate(CapacityInputs i)
    {
        int activeGames = i.Players / 2;
        double movesPerSec = activeGames * i.MovesPerGamePerSec;
        double finishesPerSec = i.AvgGameDurationSec > 0 ? (double)activeGames / i.AvgGameDurationSec : 0;

        double chessCores = i.MovesPerSecPerCore > 0 ? movesPerSec / i.MovesPerSecPerCore : 0;
        double effectiveCores = chessCores * i.CpuOverheadFactor;

        double stateMemGb = activeGames * (double)i.BytesPerActiveGame / 1_000_000_000.0;
        double connMemGb = (double)i.Players * i.BytesPerConnection / 1_000_000_000.0;

        int nodesByConn = CeilDiv(i.Players, i.ConnectionsPerNode);
        int nodesByCpu = (int)Math.Ceiling(effectiveCores / i.NodeVCpu);
        double usableRamGb = i.NodeRamGb * i.NodeRamUsableFraction;
        int nodesByMem = usableRamGb > 0 ? (int)Math.Ceiling((stateMemGb + connMemGb) / usableRamGb) : 0;

        int baseNodes = Math.Max(nodesByConn, Math.Max(nodesByCpu, nodesByMem));
        int gameServerNodes = baseNodes + Math.Max(1, (int)Math.Ceiling(baseNodes * i.FailoverHeadroom));

        double redisPub = movesPerSec; // ~ публикаций в backplane на ход (без co-location игроков партии)
        bool redisCluster = redisPub > i.RedisPubPerSecPerInstance;

        return new CapacitySizing(
            i.Players, activeGames, movesPerSec, finishesPerSec,
            chessCores, effectiveCores, stateMemGb, connMemGb,
            nodesByConn, nodesByCpu, nodesByMem, gameServerNodes,
            redisPub, redisCluster, finishesPerSec);
    }

    private static int CeilDiv(int a, int b) => b <= 0 ? 0 : (a + b - 1) / b;
}
