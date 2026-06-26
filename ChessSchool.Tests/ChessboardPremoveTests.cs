using Bunit;
using ChessSchool.Arena.Components;
using Microsoft.AspNetCore.Components;

namespace ChessSchool.Tests;

/// <summary>Проверяет логику предхода (premove) у доски: на чужом ходу ход копится, на своём — исполняется.</summary>
public class ChessboardPremoveTests : TestContext
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    // Индекс кнопки клетки для НЕперевёрнутой доски (белые снизу): row*8+col, row = 8-rank, col = file-'a'.
    private static int Sq(string s) => (8 - (s[1] - '0')) * 8 + (s[0] - 'a');

    [Fact]
    public void Premove_DuringOpponentTurn_IsQueued_ThenExecutesOnMyTurn()
    {
        (string From, string To, string? Promotion)? move = null;
        var cut = Render<Chessboard>(p => p
            .Add(c => c.Fen, StartFen)
            .Add(c => c.FlipForBlack, false) // игрок играет белыми
            .Add(c => c.MyTurn, false)       // сейчас ход соперника
            .Add(c => c.OnMove, EventCallback.Factory.Create<(string, string, string?)>(this, m => move = m)));

        // Кликаем свою пешку e2, затем поле e4 — задаём предход.
        cut.FindAll("button.sq")[Sq("e2")].Click();
        cut.FindAll("button.sq")[Sq("e4")].Click();

        // На чужом ходу ход НЕ отправляется, но поля предхода подсвечены.
        Assert.Null(move);
        Assert.Contains("premove", cut.FindAll("button.sq")[Sq("e2")].ClassList);
        Assert.Contains("premove", cut.FindAll("button.sq")[Sq("e4")].ClassList);

        // Наступает наш ход — предход исполняется автоматически.
        cut.Render(p => p.Add(c => c.MyTurn, true));

        Assert.NotNull(move);
        Assert.Equal(("e2", "e4", (string?)null), move);
    }

    [Fact]
    public void Premove_CanBeCancelled_ByClickingElsewhere()
    {
        (string From, string To, string? Promotion)? move = null;
        var cut = Render<Chessboard>(p => p
            .Add(c => c.Fen, StartFen)
            .Add(c => c.FlipForBlack, false)
            .Add(c => c.MyTurn, false)
            .Add(c => c.OnMove, EventCallback.Factory.Create<(string, string, string?)>(this, m => move = m)));

        cut.FindAll("button.sq")[Sq("e2")].Click();
        cut.FindAll("button.sq")[Sq("e4")].Click();
        Assert.Contains("premove", cut.FindAll("button.sq")[Sq("e4")].ClassList);

        // Клик по пустой клетке снимает предход.
        cut.FindAll("button.sq")[Sq("h5")].Click();
        Assert.DoesNotContain("premove", cut.FindAll("button.sq")[Sq("e4")].ClassList);

        // На своём ходу ничего не исполняется (предход отменён).
        cut.Render(p => p.Add(c => c.MyTurn, true));
        Assert.Null(move);
    }
}
