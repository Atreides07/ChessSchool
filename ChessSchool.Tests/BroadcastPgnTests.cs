using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

/// <summary>Разбор «живого» мульти-партийного PGN трансляции в доски с позициями. Чистые функции — без сети.</summary>
public class BroadcastPgnTests
{
    // Двухпартийный фид с тегами Board и комментариями [%clk] (как в фидах lichess/DGT).
    private const string Feed = """
    [Event "Test Masters"]
    [Site "Loc"]
    [Round "1"]
    [Board "1"]
    [White "Carlsen, Magnus"]
    [Black "Nepomniachtchi, Ian"]
    [WhiteElo "2839"]
    [BlackElo "2789"]
    [Result "1-0"]

    1. e4 { [%clk 1:30:00] } e5 2. Nf3 { [%clk 1:29:55] } Nc6 3. Bb5 a6 1-0

    [Event "Test Masters"]
    [Site "Loc"]
    [Round "1"]
    [Board "2"]
    [White "Caruana, Fabiano"]
    [Black "Firouzja, Alireza"]
    [Result "*"]

    1. d4 Nf6 2. c4 *
    """;

    [Fact]
    public void SplitGames_SplitsByEventTag()
    {
        var games = BroadcastPgn.SplitGames(Feed);
        Assert.Equal(2, games.Count);
        Assert.Contains("Carlsen", games[0]);
        Assert.Contains("Caruana", games[1]);
    }

    [Fact]
    public void SplitGames_NoTags_TreatedAsSingleGame()
    {
        Assert.Single(BroadcastPgn.SplitGames("1. e4 e5 2. Nf3 *"));
        Assert.Empty(BroadcastPgn.SplitGames("   "));
    }

    [Fact]
    public void Parse_ReadsHeaders_AndBoardOrder()
    {
        var boards = BroadcastPgn.Parse(Feed);

        Assert.Equal(2, boards.Count);
        Assert.Equal(1, boards[0].Board);
        Assert.Equal("Carlsen, Magnus", boards[0].White);
        Assert.Equal("Nepomniachtchi, Ian", boards[0].Black);
        Assert.Equal("2839", boards[0].WhiteElo);
        Assert.Equal("1-0", boards[0].Result);
        Assert.True(boards[0].Finished);
        Assert.Equal(2, boards[1].Board);
        Assert.False(boards[1].Finished); // результат "*" — партия идёт
    }

    [Fact]
    public void Parse_ReplaysMoves_IntoFensAndLastMove()
    {
        var board1 = BroadcastPgn.Parse(Feed)[0];

        // 1.e4 e5 2.Nf3 Nc6 3.Bb5 a6 — 6 полуходов, несмотря на комментарии [%clk].
        Assert.Equal(6, board1.PlyCount);
        Assert.Equal("e4", board1.Plies[0].San);
        Assert.Equal("e2", board1.Plies[0].From);
        Assert.Equal("e4", board1.Plies[0].To);
        // Текущая позиция = после последнего полухода (a6), и это её клетки последнего хода.
        Assert.Equal(board1.Plies[^1].Fen, board1.Fen);
        Assert.Equal("a7", board1.LastFrom);
        Assert.Equal("a6", board1.LastTo);
        // FEN после 1.e4: пешка на e4, ход чёрных.
        Assert.StartsWith("rnbqkbnr/pppppppp/8/8/4P3", board1.Plies[0].Fen);
    }

    [Fact]
    public void Parse_SkipsGamesWithoutPlayers()
    {
        const string junk = """
        [Event "Empty"]
        [Site "x"]

        *
        """;
        Assert.Empty(BroadcastPgn.Parse(junk));
    }

    [Fact]
    public void Parse_NoMoves_CurrentPositionIsStart()
    {
        const string notStarted = """
        [Event "Round 2"]
        [Board "1"]
        [White "A"]
        [Black "B"]
        [Result "*"]

        *
        """;
        var b = BroadcastPgn.Parse(notStarted).Single();
        Assert.Equal(0, b.PlyCount);
        Assert.Equal(b.StartFen, b.Fen);
        Assert.Null(b.LastFrom);
    }

    [Fact]
    public void Parse_RespectsMaxBoards()
    {
        var boards = BroadcastPgn.Parse(Feed, maxBoards: 1);
        Assert.Single(boards);
    }
}
