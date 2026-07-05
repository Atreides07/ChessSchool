using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>
/// Каталог-расписание Арены: будущие турниры синтезируются из расписания (без активации грейнов) —
/// это держит главную быстрой. Проверяем корректность синтеза и консистентность листинга/кэша.
/// </summary>
public class ArenaDirectoryGrainTests
{
    private sealed class FakeEngine : IChessEngine
    {
        public Task<string?> GetBestMoveAsync(string fen, int skillLevel, int moveTimeMs, CancellationToken ct = default)
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
                s.AddSingleton<ArenaTelemetry>();
                s.AddSingleton(TimeProvider.System);
            });
        }
    }

    [Fact]
    public async Task ListAsync_SynthesizesFutureTournaments_AndIsConsistent()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var dir = cluster.GrainFactory.GetGrain<IArenaDirectoryGrain>(0);
            var list = await dir.ListAsync();
            var now = DateTimeOffset.Now;

            Assert.NotEmpty(list);
            // Упорядочено по времени старта.
            Assert.True(list.SequenceEqual(list.OrderBy(t => t.StartsAt)));

            // В окне есть будущие турниры (6ч вперёд) — они синтезированы: Created и без игроков.
            var future = list.Where(t => t.StartsAt > now).ToList();
            Assert.NotEmpty(future);
            Assert.All(future, t =>
            {
                Assert.Equal(TournamentStatus.Created, t.Status);
                Assert.Equal(0, t.PlayerCount);
                Assert.Equal(0, t.BotCount);
                Assert.False(string.IsNullOrWhiteSpace(t.Name)); // имя выведено из расписания
            });

            // Повторный вызов (путь кэша) даёт тот же набор турниров.
            var again = await dir.ListAsync();
            Assert.Equal(list.Select(t => t.Id), again.Select(t => t.Id));
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task Listing_HasDenseCadence_BulletHalfHour_BlitzAndRapidHourly()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var list = await cluster.GrainFactory.GetGrain<IArenaDirectoryGrain>(0).ListAsync();

            void AssertCadence(int initialSeconds, double expectMinutes)
            {
                var starts = list.Where(t => t.TimeControl.InitialSeconds == initialSeconds)
                    .Select(t => t.StartsAt).OrderBy(x => x).ToList();
                Assert.True(starts.Count >= 3, $"ожидали несколько турниров с контролем {initialSeconds}с");
                for (int i = 1; i < starts.Count; i++)
                    Assert.Equal(expectMinutes, (starts[i] - starts[i - 1]).TotalMinutes, 0);
            }

            AssertCadence(60, 30);   // Bullet — каждые 30 минут
            AssertCadence(180, 60);  // Blitz — каждый час
            AssertCadence(600, 60);  // Rapid — каждый час
        }
        finally { await cluster.StopAllSilosAsync(); }
    }
}
