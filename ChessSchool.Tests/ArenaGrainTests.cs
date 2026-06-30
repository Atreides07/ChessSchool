using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("arena"); // хранилище состояния турниров
            siloBuilder.UseInMemoryReminderService();   // грейн реализует IRemindable (reminders отключены ниже)
            siloBuilder.ConfigureServices(s =>
            {
                s.AddSingleton<ArenaNotifier>();
                s.AddSingleton<IChessEngine, FakeEngine>();
                s.AddSingleton(new ArenaRuntimeOptions(RemindersEnabled: false));
                s.AddSingleton<IAnalytics, NoopAnalytics>();
            });
        }
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
            await t.JoinAsync("user-b", "Игрок B");
            await t.SeekAsync("user-a");
            await t.SeekAsync("user-b"); // оба нажали «подобрать соперника» → мгновенный пейринг

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
    public async Task PeekSummary_OnRunningArena_FillsBotsOnFirstPeek_AndStaysConsistent()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            long start = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds();
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>($"blitz-{start}");

            // Дешёвая сводка для листинга: при первом появлении грейна продвигает турнир (счётчики верны),
            // дальше — чистое чтение. Идущий турнир без людей добирается ботами.
            var first = await t.PeekSummaryAsync();
            Assert.Equal(TournamentStatus.Running, first.Status);
            Assert.True(first.BotCount >= 2, "холодный peek идущего турнира заполняет ботов");

            // Повторный peek не ломает состояние и не теряет участников.
            var second = await t.PeekSummaryAsync();
            Assert.Equal(TournamentStatus.Running, second.Status);
            Assert.True(second.PlayerCount >= first.PlayerCount);
        }
        finally { await cluster.StopAllSilosAsync(); }
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
    public async Task SoloHuman_WaitsForOpponent_ThenGetsBotAfterGrace()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("wait-arena");
            await t.ConfigureAsync("Ожидание", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-1), 600);
            await t.JoinAsync("solo", "Один");
            await t.SeekAsync("solo"); // нажал «подобрать соперника» → вошёл в пул, ищем человека

            // Сразу после нажатия соперника-бота ещё нет — даём время найти человека.
            var immediate = await t.GetStateAsync("solo");
            Assert.True(immediate.Joined);
            Assert.True(immediate.Seeking);
            Assert.Null(immediate.MyGame);

            // Спустя время ожидания (10с) к человеку подключается бот и начинается партия.
            await Task.Delay(TimeSpan.FromSeconds(11));
            var after = await t.GetStateAsync("solo");

            Assert.NotNull(after.MyGame);
            // Соперник — бот: признак несёт флаг IsBot в DTO (имя без эмодзи-префикса, в UI рисуется тег).
            var opponentIsBot = after.MyGame!.MyColor == PieceColor.White ? after.MyGame.BlackIsBot : after.MyGame.WhiteIsBot;
            Assert.True(opponentIsBot);
            // Тот же флаг проброшен в таблицу лидеров: человек — не бот, бот — помечен.
            Assert.Contains(after.Standings, r => !r.IsBot);
            Assert.Contains(after.Standings, r => r.IsBot);
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
    public async Task Summary_MarksJoined_ForParticipantOnly()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("joined-arena");
            await t.ConfigureAsync("Мой", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-1), 600);
            await t.JoinAsync("me", "Я");

            var mine = await t.GetSummaryAsync("me");
            var other = await t.GetSummaryAsync("stranger");
            var anon = await t.GetSummaryAsync();

            Assert.True(mine.Joined);     // участник — подсвечиваем
            Assert.False(other.Joined);   // чужой sub — нет
            Assert.False(anon.Joined);    // аноним — нет
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task JoinedHumans_AreNotAutoMatched_UntilSeek()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("seek-arena");
            await t.ConfigureAsync("Подбор", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-1), 600);

            await t.JoinAsync("u1", "Игрок 1");
            await t.JoinAsync("u2", "Игрок 2");

            // Оба записаны, но «подобрать соперника» не нажимали — партий нет даже спустя грейс бота (10с):
            // соперник (ни человек, ни бот) автоматически не назначается.
            await Task.Delay(TimeSpan.FromSeconds(12));
            var idle = await t.GetStateAsync("u1");
            Assert.True(idle.Joined);
            Assert.False(idle.Seeking);
            Assert.Null(idle.MyGame);

            // Нажали оба — два ищущих человека спариваются сразу.
            await t.SeekAsync("u1");
            await t.SeekAsync("u2");
            var paired = await t.GetStateAsync("u1");
            Assert.NotNull(paired.MyGame);
            Assert.False(paired.Seeking); // уже играет — в пуле подбора больше нет
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task DrawOffer_BetweenHumans_AcceptEndsInDraw()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("draw-arena");
            await t.ConfigureAsync("Ничья", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-1), 600);

            await t.JoinAsync("a", "Игрок A");
            await t.JoinAsync("b", "Игрок B");
            await t.SeekAsync("a");
            await t.SeekAsync("b"); // спарились (оба люди)

            // A предлагает ничью человеку → не мгновенно, ждём ответа.
            Assert.Equal("offered", await t.OfferDrawAsync("a"));
            var bState = await t.GetStateAsync("b");
            Assert.True(bState.MyGame!.DrawOfferFromOpponent); // B видит предложение
            Assert.False((await t.GetStateAsync("a")).MyGame!.DrawOfferFromOpponent); // у A — это своё предложение

            // B принимает → партия завершена ничьёй.
            await t.AcceptDrawAsync("b");
            var done = await t.GetStateAsync("a");
            Assert.Equal(GameStatus.Finished, done.MyGame!.Status);
            Assert.Equal(GameResult.Draw, done.MyGame!.Result);
            Assert.Equal(GameEndReason.DrawAgreed, done.MyGame!.EndReason); // причина пробрасывается в DTO
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task DrawOffer_Declined_ClearsOffer_GameContinues()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("draw-decline-arena");
            await t.ConfigureAsync("Отказ", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-1), 600);
            await t.JoinAsync("a", "A");
            await t.JoinAsync("b", "B");
            await t.SeekAsync("a");
            await t.SeekAsync("b");

            await t.OfferDrawAsync("a");
            await t.DeclineDrawAsync("b");
            var state = await t.GetStateAsync("b");
            Assert.False(state.MyGame!.DrawOfferFromOpponent);     // предложение снято
            Assert.Equal(GameStatus.InProgress, state.MyGame!.Status); // партия продолжается
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task FinishedGame_StaysForPlayerAndViewers_UntilSeekNext()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("linger-arena");
            await t.ConfigureAsync("Хвост", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-1), 600);

            await t.JoinAsync("a", "Игрок A");
            await t.JoinAsync("b", "Игрок B");
            await t.SeekAsync("a");
            await t.SeekAsync("b"); // спарились
            var live = await t.GetStateAsync("a");
            Assert.NotNull(live.MyGame);
            var gameId = live.MyGame!.GameId;

            // A сдаётся — партия завершена, но доска у A не исчезает и не запускается следующая.
            await t.ResignAsync("a");
            var afterResign = await t.GetStateAsync("a");
            Assert.NotNull(afterResign.MyGame);
            Assert.Equal(GameStatus.Finished, afterResign.MyGame!.Status);
            Assert.Equal(GameEndReason.Resignation, afterResign.MyGame!.EndReason);
            Assert.False(afterResign.Seeking); // следующего соперника не ищем автоматически

            // Спустя время дольше прежнего «хвоста» (6с) доска всё ещё на месте — её держит человек.
            await Task.Delay(TimeSpan.FromSeconds(8));
            var stillThere = await t.GetStateAsync("a");
            Assert.NotNull(stillThere.MyGame);
            Assert.Contains(await t.GetBoardsAsync(), x => x.GameId == gameId); // и в трансляции для зрителей

            // A нажимает «подобрать соперника» → отцепляется от завершённой партии и входит в поиск.
            await t.SeekAsync("a");
            var seeking = await t.GetStateAsync("a");
            Assert.Null(seeking.MyGame);
            Assert.True(seeking.Seeking);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task GetBoards_OnRunningArena_ReturnsActiveGames()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("boards-arena");
            await t.ConfigureAsync("Доски", TimeControl.Blitz, DateTimeOffset.UtcNow.AddSeconds(-1), 600);

            // Идущий турнир без людей добирается ботами и спаривает их → есть активные партии.
            await t.GetSummaryAsync(); // триггерит Tick/ManageBots/PairIdlePlayers
            var boards = await t.GetBoardsAsync();

            Assert.NotEmpty(boards);
            // Полный список (для «Все игры») не урезан до 4, как лента в шапке.
            Assert.All(boards, b => Assert.False(string.IsNullOrEmpty(b.WhiteName)));
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task RunningTournament_RespectsConfiguredBotCount()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var settings = cluster.GrainFactory.GetGrain<IBotSettingsGrain>(0);

            // Blitz: держим ровно 2 ботов. Турнир по расписанию (id вида "blitz-{unix}"), стартовал недавно.
            await settings.SetCountAsync("Blitz", 2);
            long blitzStart = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds();
            var blitz = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>($"blitz-{blitzStart}");
            var blitzSummary = await blitz.GetSummaryAsync();
            Assert.Equal(TournamentStatus.Running, blitzSummary.Status);
            Assert.Equal(2, blitzSummary.BotCount);

            // Bullet: ботов отключили (0) — в пустом турнире ботов нет.
            await settings.SetCountAsync("Bullet", 0);
            long bulletStart = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeSeconds();
            var bullet = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>($"bullet-{bulletStart}");
            var bulletSummary = await bullet.GetSummaryAsync();
            Assert.Equal(TournamentStatus.Running, bulletSummary.Status);
            Assert.Equal(0, bulletSummary.BotCount);
        }
        finally { await cluster.StopAllSilosAsync(); }
    }

    [Fact]
    public async Task FinishedTournament_ExposesRealSimulatedHistoryConsistentWithScoring()
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
            Assert.True(state.Standings.Count >= 8, "симулированный турнир имеет реальный состав");
            Assert.Empty(state.Boards); // завершённый — живых партий нет

            // Таблица отсортирована по очкам по убыванию.
            for (int i = 1; i < state.Standings.Count; i++)
                Assert.True(state.Standings[i - 1].Score >= state.Standings[i].Score);

            // У каждого игрока история партий = числу партий, а сумма очков сходится по правилам арены
            // (победа +2/+4 на огне, ничья +1/+2, поражение 0) — числа реальные, не случайные.
            foreach (var s in state.Standings)
            {
                Assert.Equal(s.Games, s.Results.Count);
                Assert.Equal(s.Score, s.Results.Sum());
                Assert.All(s.Results, r => Assert.Contains(r, new[] { 0, 1, 2, 4 }));
            }

            var leader = state.Standings[0];
            Assert.Equal(1, leader.Rank);
            Assert.NotEmpty(leader.Results);

            // Детерминизм: повторная конфигурация того же id даёт ту же таблицу.
            var t2 = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>("demo-finished");
            var state2 = await t2.GetStateAsync("spectator");
            Assert.Equal(leader.Name, state2.Standings[0].Name);
            Assert.Equal(leader.Score, state2.Standings[0].Score);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task UnconfiguredGrain_SelfConfiguresFromScheduleId()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            // Прямой переход на /t/{id} без участия каталога: грейн обязан сам вывести мету из id.
            long start = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>($"blitz-{start}");

            var summary = await t.GetSummaryAsync(); // ConfigureAsync НЕ вызываем

            Assert.Equal(TournamentStatus.Finished, summary.Status); // старт 2ч назад, длительность 1ч
            Assert.Equal(180, summary.TimeControl.InitialSeconds);   // блиц 3+0 из расписания
            Assert.True(summary.PlayerCount >= 8);                   // симулированный состав
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }

    [Fact]
    public async Task Standings_SurviveGrainDeactivation()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            const string id = "persist-test";
            var t = cluster.GrainFactory.GetGrain<IArenaTournamentGrain>(id);
            await t.ConfigureFinishedDemoAsync("Blitz 3+0 22:00", new TimeControl(180, 0),
                DateTimeOffset.UtcNow.AddHours(-2), 3600);

            var before = await t.GetStateAsync("x");
            var leaderName = before.Standings[0].Name;
            var leaderScore = before.Standings[0].Score;

            // Принудительно собираем неактивные грейны — имитируем деактивацию по простою.
            await cluster.GrainFactory.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            // Новый доступ поднимает грейн заново; таблица должна прочитаться из хранилища, а не пропасть.
            var after = await cluster.GrainFactory.GetGrain<IArenaTournamentGrain>(id).GetStateAsync("x");

            Assert.Equal(before.Standings.Count, after.Standings.Count);
            Assert.Equal(leaderName, after.Standings[0].Name);
            Assert.Equal(leaderScore, after.Standings[0].Score);
        }
        finally
        {
            await cluster.StopAllSilosAsync();
        }
    }
}
