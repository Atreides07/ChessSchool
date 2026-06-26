using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>Поднимает реальный Orleans-кластер и проверяет состояние партии турнира.</summary>
public class ArenaGrainTests
{
    private sealed class FakeEngine : IChessEngine
    {
        public Task<string?> GetBestMoveAsync(string fen, int skillLevel, int moveTimeMs, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) =>
            siloBuilder.ConfigureServices(s =>
            {
                s.AddSingleton<ArenaNotifier>();
                s.AddSingleton<IChessEngine, FakeEngine>();
            });
    }

    [Fact]
    public async Task NewlyPairedGame_StartsFromFullInitialPosition()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("test-arena");
            await t.ConfigureAsync("Тест", TimeControl.Bullet, DateTimeOffset.UtcNow.AddSeconds(-1), 300);

            await t.JoinAsync("user-a", "Игрок A");
            await t.JoinAsync("user-b", "Игрок B"); // турнир идёт → мгновенный пейринг

            var state = await t.GetStateAsync("user-a");

            Assert.NotNull(state.MyGame);
            Assert.Equal(
                "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
                state.MyGame!.Fen);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task RunningArena_WithoutHumans_FillsWithBots()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("bot-fill-arena");
            // Турнир уже идёт (старт секунду назад).
            await t.ConfigureAsync("Бот-тест", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-1), 600);

            var summary = await t.GetSummaryAsync();

            Assert.Equal(TournamentStatus.Running, summary.Status);
            Assert.Equal(0, summary.HumanCount);        // людей нет
            Assert.True(summary.BotCount >= 2, "идущий турнир без людей добирается ботами");
            Assert.Equal(summary.BotCount, summary.PlayerCount); // все участники — боты
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task CreatedArena_AllowsRegistration_WithoutBots()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("future-arena");
            // Турнир в будущем — открыта регистрация, ботов нет, играть нельзя.
            await t.ConfigureAsync("Будущий", TimeControl.Blitz, DateTimeOffset.UtcNow.AddHours(1), 600);
            await t.JoinAsync("human-1", "Человек");

            var summary = await t.GetSummaryAsync();
            Assert.Equal(TournamentStatus.Created, summary.Status);
            Assert.Equal(1, summary.HumanCount);
            Assert.Equal(0, summary.BotCount); // до старта ботов не добавляем

            var state = await t.GetStateAsync("human-1");
            Assert.True(state.Joined);
            Assert.Null(state.MyGame); // партий до старта нет
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task FinishedDemo_ExposesStandingsWithPerGameResultsAndMeta()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("demo-finished");
            var startedAt = DateTimeOffset.UtcNow.AddHours(-2);
            await t.ConfigureFinishedDemoAsync("Blitz 3+0 22:00", new TimeControl(180, 0), startedAt, 3600);

            var state = await t.GetStateAsync("spectator"); // анонимный просмотр результатов

            Assert.Equal(TournamentStatus.Finished, state.Status);
            Assert.Equal(3600, state.DurationSeconds);
            Assert.Equal(180, state.TimeControl.InitialSeconds);
            Assert.Equal(4, state.Standings.Count);

            // Лидер первый, у него заполнена история партий и счёт совпадает.
            var leader = state.Standings[0];
            Assert.Equal(1, leader.Rank);
            Assert.Equal("ArenaHost_0", leader.Name);
            Assert.Equal(20, leader.Score);
            Assert.NotEmpty(leader.Results);
            Assert.Equal(14, leader.Games);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }
}
