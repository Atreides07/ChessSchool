using System.Diagnostics;
using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

// ---------------------------------------------------------------------------------------------------
// Нагрузочный тест Арены на ЯРУСЕ ГРЕЙНА (единица масштабирования — грейн-на-турнир, single-writer).
//
// Почему не через SignalR-хаб: ArenaHub аутентифицируется по COOKIE-сессии → 5000 реальных клиентов
// потребовали бы 5000 OIDC-логинов, и на одной машине мы мерили бы генератор нагрузки, а не Арену
// (та же оговорка, что в docs/CAPACITY_PLANNING.md §0/§8 про невоспроизводимость транспорта локально).
// Поэтому здесь измеряется то, что МОЖНО измерить честно на одной машине: стоимость состояния и тика
// грейна турнира под 5000 синтетических участников (docs/CAPACITY_PLANNING.md §8: «K турниров × M
// участников, рост до деградации тика»). Транспортный ярус (WS/SignalR) — не воспроизводится, помечен.
// ---------------------------------------------------------------------------------------------------

int players = ArgInt(args, "--players", 5000);
int sustainSeconds = ArgInt(args, "--sustain-seconds", 15);
string scenario = ArgStr(args, "--scenario", "all");

Console.WriteLine("=== Arena load test (grain tier) ===");
Console.WriteLine($"Машина: {Environment.ProcessorCount} лог. ядер; players={players}, sustain={sustainSeconds}s, scenario={scenario}");
Console.WriteLine("Ярус: состояние+тик грейна турнира (Orleans in-proc silo). Транспорт (SignalR/cookie) НЕ участвует.\n");

var cluster = new TestClusterBuilder()
    .AddSiloBuilderConfigurator<SiloConfigurator>()
    .AddClientBuilderConfigurator<ClientConfigurator>()
    .Build();

var swBoot = Stopwatch.StartNew();
await cluster.DeployAsync();
swBoot.Stop();
Console.WriteLine($"Силос поднят за {swBoot.ElapsedMilliseconds} мс.\n");

try
{
    // Боты выключены — изолируем нагрузку ЛЮДЕЙ (пейринг/тик человек-vs-человек), а не бот-черн.
    var settings = cluster.GrainFactory.GetGrain<IBotSettingsGrain>(0);
    foreach (var type in new[] { "Bullet", "Blitz", "Rapid" }) await settings.SetCountAsync(type, 0);

    long memBefore = GcMem();

    if (scenario is "all" or "storm") await StormScenario(cluster, players);
    if (scenario is "all" or "horizontal") await HorizontalScenario(cluster, players);
    if (scenario is "all" or "sustain") await SustainScenario(cluster, players, sustainSeconds);

    long memAfter = GcMem();
    Console.WriteLine("\n=== Память ===");
    Console.WriteLine($"Managed heap: до {Mb(memBefore)} МБ → после {Mb(memAfter)} МБ (Δ {Mb(memAfter - memBefore)} МБ)");
    Console.WriteLine($"Working set процесса: {Mb(Environment.WorkingSet)} МБ");

    Console.WriteLine("\n=== Оговорки (честность измерения) ===");
    Console.WriteLine("• Измерен ЯРУС ГРЕЙНА (состояние+тик) на in-proc тест-кластере (2 силоса) — не транспорт.");
    Console.WriteLine("• Один грейн турнира однопоточный (turn-based concurrency) → это верхняя граница нагрузки");
    Console.WriteLine("  на ОДИН турнир. Реальный масштаб — горизонтально: грейн-на-турнир по силосам (сценарий 2).");
    Console.WriteLine("• Транспорт (WS/SignalR плотность, cookie-auth) и Redis-backplane — по docs/CAPACITY_PLANNING.md §6 (staging).");
}
finally
{
    await cluster.StopAllSilosAsync();
}
return;

// ------------------------------- Сценарий 1: шторм в ОДИН грейн -------------------------------
// Регистрация+подбор N участников в ОДНОМ турнире + стоимость одного тика при росте населения.
// Вскрывает: каждый Join/Seek/Get внутри зовёт Tick() (скан партий+игроков) → на одном грейне это O(N)
// на операцию, а «шторм» из N join'ов — O(N²). Здесь это видно как рост времени одного тика с N.
async Task StormScenario(TestCluster c, int target)
{
    Console.WriteLine("=== Сценарий 1: шторм в ОДИН грейн (single-writer) ===");
    Console.WriteLine("N     | join ops/с | seek ops/с | живых партий | тик пустой, мс | тик с партиями, мс");
    Console.WriteLine("------+------------+------------+--------------+----------------+-------------------");

    foreach (int n in Sizes(target))
    {
        var id = $"storm-{n}-{Guid.NewGuid():N}";
        var g = c.GrainFactory.GetGrain<IArenaTournamentGrain>(id);
        try
        {
            await g.ConfigureAsync($"Storm {n}", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-2), 3600);

            // Регистрация N игроков (конкурентно — клиент шлёт, грейн сериализует по одному).
            var joinSw = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, n).Select(i => g.JoinAsync($"u{i}", $"Игрок {i}")));
            joinSw.Stop();

            // Один тик над N игроков, 0 партий (все зарегистрированы, никто не ищет).
            double tickEmpty = await TimeOp(() => g.GetSummaryAsync());

            // Все нажимают «подобрать соперника» → N/2 партий человек-vs-человек.
            var seekSw = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, n).Select(i => g.SeekAsync($"u{i}")));
            seekSw.Stop();

            int liveGames = (await g.GetBoardsAsync()).Count;

            // Один тик над N игроков + N/2 партий (скан клоков всех партий).
            double tickLive = await TimeOp(() => g.GetSummaryAsync());

            Console.WriteLine($"{n,-5} | {Rate(n, joinSw),10:N0} | {Rate(n, seekSw),10:N0} | {liveGames,12} | {tickEmpty,14:F2} | {tickLive,18:F2}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{n,-5} | ⚠ таймаут/сбой: {ex.GetType().Name} — грейн не справился с бёрстом (см. вывод ниже)");
        }
    }
    Console.WriteLine("Вывод: тик растёт с населением (O(N)); шторм регистраций в один турнир — квадратичный.\n");
}

// ------------------------------- Сценарий 2: горизонтально по грейнам -------------------------------
// Те же N участников, но РАСПРЕДЕЛЁННЫЕ по K турнирам. Показывает рычаг масштабирования Арены:
// грейн-на-турнир исполняется параллельно (turn-based concurrency между активациями) → агрегатная
// пропускная способность растёт с числом турниров, в отличие от одного «толстого» турнира (сценарий 1).
async Task HorizontalScenario(TestCluster c, int total)
{
    Console.WriteLine("=== Сценарий 2: горизонтально — N участников по K турнирам ===");
    Console.WriteLine("K турниров | участников/турнир | всего join+seek, мс | агрегат ops/с");
    Console.WriteLine("-----------+-------------------+---------------------+--------------");

    foreach (int k in new[] { 1, 5, 10, 25 }.Where(k => k <= total))
    {
        int per = total / k;
        var grains = Enumerable.Range(0, k)
            .Select(j => c.GrainFactory.GetGrain<IArenaTournamentGrain>($"horiz-{k}-{j}-{Guid.NewGuid():N}"))
            .ToArray();
        foreach (var g in grains)
            await g.ConfigureAsync("Horiz", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-2), 3600);

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(grains.Select(g => Task.Run(async () =>
        {
            await Task.WhenAll(Enumerable.Range(0, per).Select(i => g.JoinAsync($"u{i}", $"Игрок {i}")));
            await Task.WhenAll(Enumerable.Range(0, per).Select(i => g.SeekAsync($"u{i}")));
        })));
        sw.Stop();

        double ops = (per * 2.0 * k) / sw.Elapsed.TotalSeconds; // join+seek на всех
        Console.WriteLine($"{k,-10} | {per,-17} | {sw.ElapsedMilliseconds,19:N0} | {ops,12:N0}");
    }
    Console.WriteLine("Вывод: рост K (турниров) поднимает агрегатную пропускную способность — так Арена масштабируется.\n");
}

// ------------------------------- Сценарий 3: устойчивый тик под живой нагрузкой -------------------------------
// N участников в одном турнире → N/2 живых партий. Держим окно T секунд, пока реальный таймер грейна
// (500 мс) ведёт часы всех партий, и КАЖДЫЕ 250 мс шлём пробу GetSummaryAsync, измеряя её round-trip.
// Проба сериализуется за тиком (single-writer) → её задержка = отзывчивость грейна под нагрузкой тика.
async Task SustainScenario(TestCluster c, int n, int seconds)
{
    Console.WriteLine($"=== Сценарий 3: устойчивый тик, {n} участников ({n / 2} партий), {seconds}с ===");
    var id = $"sustain-{Guid.NewGuid():N}";
    var g = c.GrainFactory.GetGrain<IArenaTournamentGrain>(id);
    await g.ConfigureAsync("Sustain", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-2), 3600);
    await Task.WhenAll(Enumerable.Range(0, n).Select(i => g.JoinAsync($"u{i}", $"Игрок {i}")));
    await Task.WhenAll(Enumerable.Range(0, n).Select(i => g.SeekAsync($"u{i}")));

    int gamesStart = (await g.GetBoardsAsync()).Count;
    var probe = new List<double>();
    var end = DateTimeOffset.UtcNow.AddSeconds(seconds);
    while (DateTimeOffset.UtcNow < end)
    {
        probe.Add(await TimeOp(() => g.GetSummaryAsync()));
        await Task.Delay(250);
    }
    int gamesEnd = (await g.GetBoardsAsync()).Count;

    probe.Sort();
    Console.WriteLine($"Живых партий: старт {gamesStart} → конец {gamesEnd}");
    Console.WriteLine($"Проба GetSummary (round-trip за тиком), проб {probe.Count}:");
    Console.WriteLine($"  p50 {Pct(probe, .50):F2} мс | p95 {Pct(probe, .95):F2} мс | p99 {Pct(probe, .99):F2} мс | max {probe[^1]:F2} мс");
    Console.WriteLine($"Бюджет тика — {ArenaTuning.TimerCadenceMs} мс. p99 пробы < бюджета → грейн держит тик без деградации.\n");
}

// ------------------------------- helpers -------------------------------
static int[] Sizes(int target)
{
    var s = new List<int> { 1000, 2500, 5000 }.Where(x => x < target).ToList();
    s.Add(target);
    return s.Distinct().OrderBy(x => x).ToArray();
}

static async Task<double> TimeOp(Func<Task> op)
{
    var sw = Stopwatch.StartNew();
    await op();
    sw.Stop();
    return sw.Elapsed.TotalMilliseconds;
}

static double Rate(int count, Stopwatch sw) => count / Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
static long GcMem() => GC.GetTotalMemory(forceFullCollection: true);
static double Mb(long bytes) => bytes / 1024.0 / 1024.0;

static int ArgInt(string[] a, string key, int def)
{
    int i = Array.IndexOf(a, key);
    return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : def;
}
static string ArgStr(string[] a, string key, string def)
{
    int i = Array.IndexOf(a, key);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
}

// Тот же минимальный силос, что в ChessSchool.Tests/ArenaGrainTests: память-хранилище, заглушка движка
// (бот ходит случайным легальным — Stockfish не нужен), reminders off, реальное время.
file sealed class SiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage("arena");
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.Configure<Orleans.Configuration.SiloMessagingOptions>(o => o.ResponseTimeout = TimeSpan.FromMinutes(5));
        siloBuilder.ConfigureServices(s =>
        {
            s.AddSingleton<ArenaNotifier>();
            s.AddSingleton<IChessEngine, FakeEngine>();
            s.AddSingleton(new ArenaRuntimeOptions(RemindersEnabled: false));
            s.AddSingleton<IAnalytics, NoopAnalytics>();
            s.AddSingleton<ArenaTelemetry>();
            s.AddSingleton(TimeProvider.System);
        });
    }
}

// Поднимаем таймаут ответа Orleans (дефолт 30с): при 5000 регистраций в ОДИН грейн операции O(N²)
// пробивают 30с — а нам нужно ИЗМЕРИТЬ реальное время до конца, а не падать. Так виден масштаб проблемы.
file sealed class ClientConfigurator : IClientBuilderConfigurator
{
    public void Configure(Microsoft.Extensions.Configuration.IConfiguration configuration, IClientBuilder clientBuilder) =>
        clientBuilder.Configure<Orleans.Configuration.ClientMessagingOptions>(o => o.ResponseTimeout = TimeSpan.FromMinutes(5));
}

file sealed class FakeEngine : IChessEngine
{
    public Task<string?> GetBestMoveAsync(string fen, int skillLevel, int moveTimeMs, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
