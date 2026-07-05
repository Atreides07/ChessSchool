using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>
/// Детерминистичная проверка ТАЙМИНГ-решений грейна турнира через инжектированный <see cref="TimeProvider"/>:
/// раньше время бралось из `DateTimeOffset.UtcNow` и такие сценарии нельзя было тестировать без `Task.Delay`.
/// Теперь часы управляемые — флаг по времени проверяется без сна и без флака.
/// </summary>
public class ArenaTimeTests
{
    // Управляемые часы, общие для тест-силоса и теста (TestingHost — тот же процесс).
    private sealed class MutableTimeProvider : TimeProvider
    {
        public static readonly MutableTimeProvider Instance = new();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset t) => _now = t;
        public void Advance(TimeSpan d) => _now = _now.Add(d);
    }

    private sealed class FakeEngine : IChessEngine
    {
        public Task<string?> GetBestMoveAsync(string fen, int skill, int moveMs, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("arena");
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.ConfigureServices(s =>
            {
                s.AddSingleton<ArenaNotifier>();
                s.AddSingleton<IChessEngine, FakeEngine>();
                s.AddSingleton(new ArenaRuntimeOptions(RemindersEnabled: false));
                s.AddSingleton<IAnalytics, NoopAnalytics>();
                s.AddSingleton<TimeProvider>(MutableTimeProvider.Instance); // управляемые часы
            });
        }
    }

    [Fact]
    public async Task PlayerFlagsOnTime_WhenClockAdvancedPastLimit()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        MutableTimeProvider.Instance.Set(t0);

        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("time-arena");
            await t.ConfigureAsync("Тайминг", TimeControl.Bullet, t0.AddSeconds(-1), 3600); // Bullet = 60с на игрока

            await t.JoinAsync("user-a", "A");
            await t.JoinAsync("user-b", "B");
            await t.SeekAsync("user-a");
            await t.SeekAsync("user-b"); // мгновенный пейринг

            var game = (await t.GetStateAsync("user-a")).MyGame;
            Assert.NotNull(game);
            var whiteSub = game!.MyColor == PieceColor.White ? "user-a" : "user-b";

            // Белые «думают» 120с при лимите 60с → флаг на ходе. Часы двигаем детерминированно, без сна.
            MutableTimeProvider.Instance.Advance(TimeSpan.FromSeconds(120));
            await t.MoveAsync(whiteSub, new MoveInput("e2", "e4"));

            // Партия завершена по времени, победа у чёрных (linger не истёк — часы «заморожены» на t0+120с).
            var after = (await t.GetStateAsync(whiteSub)).MyGame;
            Assert.NotNull(after);
            Assert.Equal(GameStatus.Finished, after!.Status);
            Assert.Equal(GameResult.BlackWins, after.Result);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }
}
