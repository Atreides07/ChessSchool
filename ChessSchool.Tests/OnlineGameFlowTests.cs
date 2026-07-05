using System.Collections.Concurrent;
using ChessSchool.Contracts;
using ChessSchool.GameServer.Grains;
using ChessSchool.GameServer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>
/// Сквозной интеграционный тест онлайн-игры на реальном Orleans-кластере: матчмейкинг сводит двух
/// игроков в одну партию, ходы идут через грейн партии, завершение архивируется. Это серверная логика,
/// которую <c>/play</c> дёргает через SignalR-хаб (<see cref="ChessSchool.GameServer.Hubs.GameHub"/>) —
/// сам хаб лишь тонко проксирует те же вызовы (FindMatch→FindMatchAsync, Move→TryMoveAsync,
/// Resign→ResignAsync, JoinGame→GetStateAsync), поэтому тест бьёт по настоящему контуру.
/// </summary>
public class OnlineGameFlowTests
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    /// <summary>Архивы партий, перехваченные фейковым клиентом (силос in-process → статик виден тесту).</summary>
    private static readonly ConcurrentQueue<ArchiveGameRequest> Archived = new();

    private sealed class CapturingArchiveClient : IGameArchiveClient
    {
        public Task ArchiveAsync(ArchiveGameRequest request, CancellationToken ct = default)
        {
            Archived.Enqueue(request);
            return Task.CompletedTask;
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.ConfigureServices(s =>
        {
            // GameGrain зовёт архивацию и аналитику — обе подменяем (без HTTP к API, без PostHog).
            s.AddSingleton<IGameArchiveClient, CapturingArchiveClient>();
            s.AddSingleton<IAnalytics, NoopAnalytics>();
            // Короткий таймаут матчмейкинга — чтобы тест таймаута не ждал 60 секунд.
            s.AddSingleton(new MatchmakingOptions(TimeSpan.FromSeconds(1)));
        });
    }

    private static TestCluster NewCluster()
    {
        Archived.Clear();
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        return cluster;
    }

    /// <summary>Сводит двух игроков через матчмейкинг (как два браузера на /play) и возвращает их пары.</summary>
    private static async Task<(MatchFound White, MatchFound Black)> PairAsync(TestCluster cluster)
    {
        var mm = cluster.GrainFactory.GetGrain<IMatchmakingGrain>(TimeControl.Blitz.ToString());
        // Алиса ищет первой и «висит» в ожидании (не await!) — встаёт в очередь.
        var aliceTask = mm.FindMatchAsync(new MatchRequest("alice", "Алиса", 1200, TimeControl.Blitz));
        await Task.Delay(250); // даём грейну обработать заявку Алисы и встать в очередь
        // Боб ищет вторым — мгновенно спаривается с ждущей Алисой.
        var bob = await mm.FindMatchAsync(new MatchRequest("bob", "Боб", 1300, TimeControl.Blitz));
        var alice = await aliceTask;
        Assert.NotNull(alice); // спарились — оба не null
        Assert.NotNull(bob);
        return (alice!, bob!); // ждавший (Алиса) получает белые
    }

    [Fact]
    public async Task Matchmaking_PairsTwoPlayers_IntoOneInitializedGame()
    {
        var cluster = NewCluster();
        await cluster.DeployAsync();
        try
        {
            var (white, black) = await PairAsync(cluster);

            // Оба попали в одну партию, цвета противоположны, соперник указан верно.
            Assert.Equal(white.GameId, black.GameId);
            Assert.Equal(PieceColor.White, white.Color);
            Assert.Equal(PieceColor.Black, black.Color);
            Assert.Equal("bob", white.OpponentId);
            Assert.Equal("alice", black.OpponentId);

            // Партия инициализирована: статус InProgress, начальная позиция, белые — ждавший игрок.
            var state = await cluster.GrainFactory.GetGrain<IGameGrain>(white.GameId).GetStateAsync();
            Assert.NotNull(state);
            Assert.Equal(GameStatus.InProgress, state!.Status);
            Assert.Equal(StartFen, state.Fen);
            Assert.Equal("alice", state.WhitePlayerId);
            Assert.Equal("bob", state.BlackPlayerId);
            Assert.Equal(PieceColor.White, state.Turn);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task Move_RejectsNonParticipant_WrongTurn_AndIllegalMove()
    {
        var cluster = NewCluster();
        await cluster.DeployAsync();
        try
        {
            var (white, _) = await PairAsync(cluster);
            var game = cluster.GrainFactory.GetGrain<IGameGrain>(white.GameId);

            // Чужой пользователь не может ходить в этой партии.
            var ghost = await game.TryMoveAsync("ghost", new MoveInput("e2", "e4"));
            Assert.False(ghost.Accepted);
            Assert.Equal("Вы не участник этой партии.", ghost.Error);

            // Чёрные не могут ходить первыми — сейчас не их ход.
            var blackFirst = await game.TryMoveAsync("bob", new MoveInput("e7", "e5"));
            Assert.False(blackFirst.Accepted);
            Assert.Equal("Сейчас не ваш ход.", blackFirst.Error);

            // Недопустимый ход белых отклоняется.
            var illegal = await game.TryMoveAsync("alice", new MoveInput("e2", "e5"));
            Assert.False(illegal.Accepted);
            Assert.Equal("Недопустимый ход.", illegal.Error);

            // Легальный ход белых принимается, очередь переходит к чёрным.
            var legal = await game.TryMoveAsync("alice", new MoveInput("e2", "e4"));
            Assert.True(legal.Accepted);
            Assert.Null(legal.Error);
            Assert.Equal(PieceColor.Black, legal.State!.Turn);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task FullGame_ToCheckmate_FinishesAndArchives()
    {
        var cluster = NewCluster();
        await cluster.DeployAsync();
        try
        {
            var (white, _) = await PairAsync(cluster);
            var game = cluster.GrainFactory.GetGrain<IGameGrain>(white.GameId);

            // «Детский мат» дурака: 1. f3 e5 2. g4 Qh4#
            Assert.True((await game.TryMoveAsync("alice", new MoveInput("f2", "f3"))).Accepted);
            Assert.True((await game.TryMoveAsync("bob", new MoveInput("e7", "e5"))).Accepted);
            Assert.True((await game.TryMoveAsync("alice", new MoveInput("g2", "g4"))).Accepted);
            var mate = await game.TryMoveAsync("bob", new MoveInput("d8", "h4"));

            Assert.True(mate.Accepted);
            Assert.Equal(GameStatus.Finished, mate.State!.Status);
            Assert.Equal(GameResult.BlackWins, mate.State.Result);
            Assert.Equal(GameEndReason.Checkmate, mate.State.EndReason);

            // Завершённая партия ушла в архив (триггер пересчёта рейтинга в API).
            Assert.True(Archived.TryDequeue(out var archived));
            Assert.Equal(white.GameId, archived!.GameId);
            Assert.Equal("alice", archived.WhiteUserSub);
            Assert.Equal("bob", archived.BlackUserSub);
            Assert.Equal(GameResult.BlackWins, archived.Result);
            Assert.Equal(GameEndReason.Checkmate, archived.EndReason);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task TimedOutSeeker_ReturnsNull_AndIsPurged_NotPairedWithLaterPlayer()
    {
        var cluster = NewCluster();
        await cluster.DeployAsync();
        try
        {
            var mm = cluster.GrainFactory.GetGrain<IMatchmakingGrain>(TimeControl.Blitz.ToString());

            // Алиса ищет и не дожидается соперника — окно ожидания истекает (1с в тестовом силосе).
            // Возвращается null (НЕ исключение) — клиент повторил бы вызов; одинокий искатель не видит ошибки.
            Assert.Null(await mm.FindMatchAsync(new MatchRequest("alice", "Алиса", 1200, TimeControl.Blitz)));

            // Боб приходит ПОСЛЕ ухода Алисы. Её протухшая заявка вычищена (Tcs отменён) — Боб НЕ спаривается
            // с «призраком», а сам встаёт в ожидание и тоже получает null по истечении окна.
            Assert.Null(await mm.FindMatchAsync(new MatchRequest("bob", "Боб", 1300, TimeControl.Blitz)));
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task Resign_FinishesGame_AndArchives()
    {
        var cluster = NewCluster();
        await cluster.DeployAsync();
        try
        {
            var (white, _) = await PairAsync(cluster);
            var game = cluster.GrainFactory.GetGrain<IGameGrain>(white.GameId);

            // Белые сдаются — побеждают чёрные по сдаче.
            var state = await game.ResignAsync("alice");
            Assert.Equal(GameStatus.Finished, state.Status);
            Assert.Equal(GameResult.BlackWins, state.Result);
            Assert.Equal(GameEndReason.Resignation, state.EndReason);

            Assert.True(Archived.TryDequeue(out var archived));
            Assert.Equal(GameResult.BlackWins, archived!.Result);
            Assert.Equal(GameEndReason.Resignation, archived.EndReason);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }
}
