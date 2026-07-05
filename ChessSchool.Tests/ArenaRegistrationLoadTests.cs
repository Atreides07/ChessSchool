using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>
/// Регрессия на перф-долг «регистрация в турнир — O(N²) на бёрсте» (найдено нагрузочным тестом,
/// tools/arena-loadtest; см. docs/CAPACITY_PLANNING.md §8.1). До фикса JoinAsync писал ВЕСЬ стор на
/// КАЖДУЮ регистрацию (Snapshot+WriteState, O(N)) → бёрст из N join'ов = O(N²). После фикса горячие
/// пути коалесят персист: единственный писатель во время турнира — таймер тика (500 мс). Тест проверяет,
/// что бёрст регистраций даёт ≪ N записей в стор, но все игроки зарегистрированы и позже персистятся.
/// </summary>
public class ArenaRegistrationLoadTests
{
    private sealed class FakeEngine : IChessEngine
    {
        public Task<string?> GetBestMoveAsync(string fen, int skillLevel, int moveTimeMs, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    // Счётчик реальных записей стора «arena». Статический — читаем из теста (только этот класс его использует).
    private sealed class CountingGrainStorage : IGrainStorage
    {
        public static int Writes;
        private readonly Dictionary<string, object?> _state = new();
        private static string Key(string stateName, GrainId id) => $"{stateName}/{id}";

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            if (_state.TryGetValue(Key(stateName, grainId), out var v) && v is T t) grainState.State = t;
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            Interlocked.Increment(ref Writes);
            _state[Key(stateName, grainId)] = grainState.State;
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            _state.Remove(Key(stateName, grainId));
            return Task.CompletedTask;
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.ConfigureServices(s =>
            {
                // Считающее хранилище «arena» вместо memory-стора — видим число записей.
                s.AddKeyedSingleton<IGrainStorage>("arena", (_, _) => new CountingGrainStorage());
                s.AddSingleton<ArenaNotifier>();
                s.AddSingleton<IChessEngine, FakeEngine>();
                s.AddSingleton(new ArenaRuntimeOptions(RemindersEnabled: false));
                s.AddSingleton<IAnalytics, NoopAnalytics>();
                s.AddSingleton<ArenaTelemetry>();
                s.AddSingleton(TimeProvider.System);
            });
        }
    }

    [Fact]
    public async Task RegistrationBurst_CoalescesStoreWrites_ButRegistersEveryone()
    {
        CountingGrainStorage.Writes = 0;
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            const int n = 400;
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("reg-load");
            await t.ConfigureAsync("Нагрузка", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-2), 3600);
            int writesAfterConfigure = CountingGrainStorage.Writes;

            // Бёрст из N регистраций в ИДУЩИЙ турнир. До фикса это было бы ~N записей стора (O(N²) суммарно).
            await Task.WhenAll(Enumerable.Range(0, n).Select(i => t.JoinAsync($"u{i}", $"Игрок {i}")));
            int burstWrites = CountingGrainStorage.Writes - writesAfterConfigure;

            // Коалесинг: бёрст дал НА ПОРЯДКИ меньше записей, чем регистраций (в идеале 0 — таймер ещё не тикнул).
            Assert.True(burstWrites < n / 10,
                $"регистрация должна коалесить запись стора: {burstWrites} записей на {n} join (ожидалось ≪ {n / 10})");

            // Но все игроки зарегистрированы (состояние в памяти корректно и сразу видно).
            var summary = await t.GetSummaryAsync();
            int humans = summary.PlayerCount - summary.BotCount;
            Assert.True(humans >= n, $"все {n} игроков должны быть записаны, а видно {humans}");

            // Персист всё же происходит — коалесированно, таймером тика (500 мс). Ждём тик и проверяем запись.
            await Task.Delay(900);
            Assert.True(CountingGrainStorage.Writes > writesAfterConfigure,
                "накопленное состояние должно персиститься таймером тика (коалесированно)");
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task MoveBurst_CoalescesStoreWrites_ButAdvancesGame()
    {
        CountingGrainStorage.Writes = 0;
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("move-load");
            await t.ConfigureAsync("Ходы", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-2), 3600);
            await t.JoinAsync("a", "a");
            await t.JoinAsync("b", "b");
            await t.SeekAsync("a");
            await t.SeekAsync("b"); // два ищущих человека спариваются сразу

            var st = await t.GetStateAsync("a");
            Assert.NotNull(st.MyGame);
            string white = st.MyGame!.MyColor == PieceColor.White ? "a" : "b";
            string black = white == "a" ? "b" : "a";

            // Известная легальная линия (Испанка): чётные полуходы — белые, нечётные — чёрные.
            (string From, string To)[] line =
            [
                ("e2", "e4"), ("e7", "e5"), ("g1", "f3"), ("b8", "c6"),
                ("f1", "b5"), ("a7", "a6"), ("b5", "a4"), ("g8", "f6"),
            ];

            int writesBefore = CountingGrainStorage.Writes;
            ArenaGameDto? last = null;
            for (int i = 0; i < line.Length; i++)
                last = await t.MoveAsync(i % 2 == 0 ? white : black, new MoveInput(line[i].From, line[i].To, null));
            int moveWrites = CountingGrainStorage.Writes - writesBefore;

            // Ходы коалесят запись стора: мид-партийный ход в персист не идёт (в Snapshot только Players/мета,
            // не _games). До фикса это было ~N записей на N ходов. Допускаем ≤1 — таймер тика мог сработать раз.
            Assert.True(moveWrites <= 1, $"ходы должны коалесить запись стора: {moveWrites} на {line.Length} ходов");

            // Но партия реально продвинулась (ходы применились): FEN уже не стартовый.
            Assert.NotNull(last);
            Assert.NotEqual("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", last!.Fen);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }
}
