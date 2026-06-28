using ChessSchool.Capacity;

// Калькулятор ёмкости онлайн-игры: оценка железа под целевое число одновременных игроков.
//   dotnet run --project tools/capacity -- --players 100000 --bench
// Без --bench используются дефолты из docs/CAPACITY_PLANNING.md; с --bench удельные стоимости
// (ходы/с/ядро, память/партия) замеряются на этой машине. Любой параметр переопределяется флагом.

var opt = ParseArgs(args);
bool bench = opt.ContainsKey("bench");

var inputs = new CapacityInputs
{
    Players = GetInt("players", 100_000),
    MovesPerSecPerCore = GetDouble("moves-per-core", 6080),
    BytesPerActiveGame = GetInt("bytes-per-game", 20_000),
    ConnectionsPerNode = GetInt("conns-per-node", 50_000),
    NodeVCpu = GetInt("node-vcpu", 8),
    NodeRamGb = GetDouble("node-ram-gb", 16),
    CpuOverheadFactor = GetDouble("cpu-overhead", 3.0),
};

if (bench)
{
    Console.WriteLine("Замер удельных стоимостей на этой машине (Gera.Chess)…");
    double movesPerCore = Bench.MovesPerSecPerCore();
    int boardBytes = Bench.BytesPerGameBoard();
    int gameBytes = (int)(boardBytes * 1.6); // +оверхед грейна/строк (как 12КБ доска → ~20КБ партия в плане)
    Console.WriteLine($"  ходов/с/ядро (floor):   {movesPerCore:N0}");
    Console.WriteLine($"  память доски/партия:    {boardBytes:N0} Б  →  с грейном ~{gameBytes:N0} Б");
    Console.WriteLine();
    inputs = inputs with { MovesPerSecPerCore = movesPerCore, BytesPerActiveGame = gameBytes };
}

var s = CapacityModel.Estimate(inputs);

Console.WriteLine($"=== Оценка ёмкости: {s.Players:N0} одновременных игроков ===");
Console.WriteLine();
Console.WriteLine("Параметры (флаги переопределяют):");
Console.WriteLine($"  темп ходов/партию:      {inputs.MovesPerGamePerSec} полухода/с");
Console.WriteLine($"  средняя партия:         {inputs.AvgGameDurationSec} с");
Console.WriteLine($"  ходов/с/ядро:           {inputs.MovesPerSecPerCore:N0}{(bench ? " (замер)" : "")}");
Console.WriteLine($"  оверхед CPU (транспорт): ×{inputs.CpuOverheadFactor}");
Console.WriteLine($"  память/партия:          {inputs.BytesPerActiveGame:N0} Б{(bench ? " (замер)" : "")}");
Console.WriteLine($"  память/соединение:      {inputs.BytesPerConnection:N0} Б (модель)");
Console.WriteLine($"  соединений/ноду:        {inputs.ConnectionsPerNode:N0} (модель — валидировать на staging!)");
Console.WriteLine($"  нода:                   {inputs.NodeVCpu} vCPU / {inputs.NodeRamGb} ГБ");
Console.WriteLine();
Console.WriteLine("Нагрузка:");
Console.WriteLine($"  активных партий:        {s.ActiveGames:N0}");
Console.WriteLine($"  ходов/с (кластер):      {s.MovesPerSec:N0}");
Console.WriteLine($"  завершений/с:           {s.FinishesPerSec:N1}");
Console.WriteLine($"  ядер на ходы:           {s.ChessCores:N1}  →  с оверхедом ~{s.EffectiveCores:N1}");
Console.WriteLine($"  память состояния:       {s.StateMemGb:N1} ГБ");
Console.WriteLine($"  память соединений:      {s.ConnMemGb:N1} ГБ");
Console.WriteLine();
Console.WriteLine("Железо:");
Console.WriteLine($"  GameServer:             {s.GameServerNodes} нод × {inputs.NodeVCpu} vCPU / {inputs.NodeRamGb} ГБ");
Console.WriteLine($"     (по соединениям {s.NodesByConnections}, по CPU {s.NodesByCpu}, по памяти {s.NodesByMemory}, +запас на отказ)");
Console.WriteLine($"  Redis:                  {(s.RedisClusterNeeded ? "Redis Cluster (шардирование)" : "1 primary + replica")}  (~{s.RedisPubPerSec:N0} pub/с backplane)");
Console.WriteLine($"  PostgreSQL:             primary + реплики; ~{s.PostgresWritesPerSec:N0} записей/с (архивация), партиционировать Games");
Console.WriteLine($"  Stateless (Auth/Api/Web): масштабировать по RPS (несколько подов каждого)");
Console.WriteLine();
Console.WriteLine("Числа состояния измеримы; транспорт/плотность соединений — модель. Перед продом");
Console.WriteLine("обязателен распределённый E2E-тест (docs/CAPACITY_PLANNING.md §6, tools/loadtest).");

return 0;

string? Get(string key) => opt.TryGetValue(key, out var v) ? v : null;
int GetInt(string key, int dflt) => int.TryParse(Get(key), out var v) ? v : dflt;
double GetDouble(string key, double dflt) => double.TryParse(Get(key),
    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { d[key] = args[++i]; }
        else { d[key] = "true"; } // флаг без значения (напр. --bench)
    }
    return d;
}
