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
            await t.ConfigureAsync("Тест", TimeControl.Bullet, 300);

            await t.JoinAsync("user-a", "Игрок A");
            await t.JoinAsync("user-b", "Игрок B"); // второй игрок → мгновенное спаривание

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
}
