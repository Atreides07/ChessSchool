using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

public class StartPositionTests
{
    [Fact]
    public void NewGame_FenIsStandardStartPosition()
    {
        var g = new ChessGame();
        Assert.Equal("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", g.Fen);
    }

    [Fact]
    public void Move_RecordsFromToSquares()
    {
        var g = new ChessGame();
        Assert.True(g.TryMove("e2", "e4", null));
        Assert.Equal("e2", g.LastFrom);
        Assert.Equal("e4", g.LastTo);
        Assert.Null(g.CheckSquare); // шаха нет
    }

    [Fact]
    public void Check_IsReportedOnKingSquare()
    {
        var g = new ChessGame();
        // «Детский мат» по белому королю: 1.f3 e5 2.g4 Qh4#
        g.TryMove("f2", "f3", null);
        g.TryMove("e7", "e5", null);
        g.TryMove("g2", "g4", null);
        g.TryMove("d8", "h4", null);

        Assert.Equal("e1", g.CheckSquare); // белый король под шахом
        Assert.Equal("h4", g.LastTo);
    }
}
