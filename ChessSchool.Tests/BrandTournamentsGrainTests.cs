using ChessSchool.Arena;
using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>Каталог бренд-турниров (CRUD) и конфигурация грейна турнира из записи каталога.</summary>
public class BrandTournamentsGrainTests
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

    private static async Task<TestCluster> StartAsync()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        return cluster;
    }

    private static BrandTournament Sample(string slug, DateTimeOffset start) => new()
    {
        Slug = slug,
        Name = "Spring Cup",
        Description = "Кураторский блиц",
        InitialSeconds = 180,
        IncrementSeconds = 2,
        StartsAt = start,
        DurationSeconds = 3600,
        Visible = true,
    };

    [Fact]
    public async Task CatalogGrain_Crud_Works()
    {
        var cluster = await StartAsync();
        try
        {
            var grain = cluster.GrainFactory.GetGrain<IBrandTournamentsGrain>(0);
            await grain.UpsertAsync(Sample("spring-cup-2026", DateTimeOffset.UtcNow.AddHours(1)));

            Assert.Single(await grain.GetAllAsync());
            Assert.Equal("Spring Cup", (await grain.GetAsync("spring-cup-2026"))!.Name);

            Assert.True(await grain.SetVisibleAsync("spring-cup-2026", false));
            Assert.False((await grain.GetAsync("spring-cup-2026"))!.Visible);

            Assert.True(await grain.DeleteAsync("spring-cup-2026"));
            Assert.Empty(await grain.GetAllAsync());
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task ConfigureBrand_FutureTournament_IsCreatedWithSchedule()
    {
        var cluster = await StartAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("future-brand");
            await t.ConfigureBrandAsync("Spring Cup", new TimeControl(180, 2), DateTimeOffset.UtcNow.AddHours(1), 3600);

            var s = await t.GetSummaryAsync();
            Assert.Equal(TournamentStatus.Created, s.Status);
            Assert.Equal(180, s.TimeControl.InitialSeconds);
            Assert.Equal(2, s.TimeControl.IncrementSeconds);
            Assert.Equal(0, s.BotCount); // до старта ботов нет
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task ConfigureBrand_PastStart_IsRunning()
    {
        var cluster = await StartAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("live-brand");
            await t.ConfigureBrandAsync("Live Brand", new TimeControl(180, 0), DateTimeOffset.UtcNow.AddSeconds(-1), 600);

            var s = await t.GetSummaryAsync();
            Assert.Equal(TournamentStatus.Running, s.Status);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task ConfigureBrand_EditBeforeStart_UpdatesSchedule()
    {
        var cluster = await StartAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("edit-brand");
            await t.ConfigureBrandAsync("V1", new TimeControl(180, 0), DateTimeOffset.UtcNow.AddHours(2), 3600);
            // Правка до старта меняет контроль времени.
            await t.ConfigureBrandAsync("V2", new TimeControl(300, 3), DateTimeOffset.UtcNow.AddHours(3), 1800);

            var s = await t.GetSummaryAsync();
            Assert.Equal(TournamentStatus.Created, s.Status);
            Assert.Equal(300, s.TimeControl.InitialSeconds);
            Assert.Equal(1800, s.DurationSeconds);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }
}
