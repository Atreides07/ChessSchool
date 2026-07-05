using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Services;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.Tests;

/// <summary>
/// Архив арена-партий: идемпотентная запись, история по игроку (исход с его точки зрения, соперник),
/// выдача партии только участнику (приватность) и кэш разбора. Источник истины — БД.
/// </summary>
public class ArenaGameStoreTests
{
    private static ArenaDbContext NewDb()
    {
        var db = new ArenaDbContext(new DbContextOptionsBuilder<ArenaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static ArenaGameArchiveRequest Req(string id = "g1", GameResult result = GameResult.WhiteWins) =>
        // Свежий TimeControl на каждый запрос: общий статический инстанс (TimeControl.Blitz) как owned-entity
        // нельзя привязывать к нескольким владельцам в одном контексте EF (в проде каждый запрос — свой инстанс).
        new("t1", id, "white-sub", "black-sub", "Alice", "Bob", WhiteIsBot: false, BlackIsBot: true,
            "1. e4 e5 *", result, GameEndReason.Checkmate, new TimeControl(300, 2), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Archive_ThenList_FromEachPerspective()
    {
        using var db = NewDb();
        var store = new ArenaGameStore(db);
        Assert.True(await store.ArchiveAsync(Req(), default));

        var white = await store.ListForPlayerAsync("white-sub", 0, 20, default);
        var w = Assert.Single(white.Items);
        Assert.Equal(PlayerOutcome.Win, w.Outcome);
        Assert.Equal("Bob", w.OpponentName);
        Assert.True(w.OpponentIsBot);
        Assert.Equal(PieceColor.White, w.MyColor);
        Assert.False(w.Analyzed);

        var black = await store.ListForPlayerAsync("black-sub", 0, 20, default);
        var b = Assert.Single(black.Items);
        Assert.Equal(PlayerOutcome.Loss, b.Outcome);
        Assert.Equal("Alice", b.OpponentName);
        Assert.Equal(PieceColor.Black, b.MyColor);
    }

    [Fact]
    public async Task Archive_IsIdempotent_ByExternalId()
    {
        using var db = NewDb();
        var store = new ArenaGameStore(db);
        Assert.True(await store.ArchiveAsync(Req("dup"), default));
        Assert.False(await store.ArchiveAsync(Req("dup"), default)); // повтор — не дубль
        Assert.Equal(1, await db.ArenaGames.CountAsync());
    }

    [Fact]
    public async Task GetStats_CountsWinsLossesDraws_FromPlayerPerspective()
    {
        using var db = NewDb();
        var store = new ArenaGameStore(db);
        // white-sub белыми: победа, поражение, ничья.
        await store.ArchiveAsync(Req("g1", GameResult.WhiteWins), default);
        await store.ArchiveAsync(Req("g2", GameResult.BlackWins), default);
        await store.ArchiveAsync(Req("g3", GameResult.Draw), default);

        var w = await store.GetStatsAsync("white-sub", default);
        Assert.Equal((3, 1, 1, 1), (w.Total, w.Wins, w.Losses, w.Draws));

        // black-sub видит те же партии зеркально: поражение, победа, ничья.
        var b = await store.GetStatsAsync("black-sub", default);
        Assert.Equal((3, 1, 1, 1), (b.Total, b.Wins, b.Losses, b.Draws));

        Assert.Equal(0, (await store.GetStatsAsync("stranger", default)).Total); // не участник
    }

    [Fact]
    public async Task Get_OnlyParticipant_SeesGame()
    {
        using var db = NewDb();
        var store = new ArenaGameStore(db);
        await store.ArchiveAsync(Req(), default);
        var id = (await store.ListForPlayerAsync("white-sub", 0, 1, default)).Items[0].Id;

        Assert.NotNull(await store.GetForPlayerAsync(id, "white-sub", default));
        Assert.NotNull(await store.GetForPlayerAsync(id, "black-sub", default));
        Assert.Null(await store.GetForPlayerAsync(id, "stranger", default)); // не участник — приватность
    }

    [Fact]
    public async Task Analysis_CacheRoundTrip_ParticipantOnly()
    {
        using var db = NewDb();
        var store = new ArenaGameStore(db);
        await store.ArchiveAsync(Req(), default);
        var id = (await store.ListForPlayerAsync("white-sub", 0, 1, default)).Items[0].Id;

        Assert.Null(await store.GetAnalysisJsonAsync(id, "white-sub", default)); // ещё не считали
        await store.SaveAnalysisJsonAsync(id, "{\"ok\":true}", default);

        Assert.Equal("{\"ok\":true}", await store.GetAnalysisJsonAsync(id, "white-sub", default));
        Assert.Null(await store.GetAnalysisJsonAsync(id, "stranger", default)); // не участник — не отдаём
        Assert.True((await store.ListForPlayerAsync("white-sub", 0, 1, default)).Items[0].Analyzed);
    }
}
