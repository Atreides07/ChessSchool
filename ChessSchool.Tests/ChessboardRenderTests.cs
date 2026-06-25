using System.Text.RegularExpressions;
using Bunit;

namespace ChessSchool.Tests;

/// <summary>Рендер доски: со стартового FEN должны отрисоваться все 32 фигуры.</summary>
public class ChessboardRenderTests : BunitContext
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    [Fact]
    public void RendersFullStartingPosition()
    {
        var cut = Render<ChessSchool.Arena.Components.Chessboard>(p => p
            .Add(c => c.Fen, StartFen));

        // Несколько повторных рендеров с тем же FEN (имитация частых обновлений по таймеру/пушу):
        // без @key на клетках Blazor «схлопывал» доску до одной фигуры — этот тест ловит регрессию.
        for (int i = 0; i < 3; i++)
            cut.Render(p => p.Add(c => c.Fen, StartFen));

        var html = cut.Markup;

        // 64 клетки и 32 фигуры (по 16 каждого цвета) — набор Cburnett через <img> на статику.
        Assert.Equal(64, Regex.Matches(html, "<button").Count);
        Assert.Equal(32, Regex.Matches(html, "/pieces/").Count);
        Assert.Equal(16, Regex.Matches(html, "/pieces/w").Count);
        Assert.Equal(16, Regex.Matches(html, "/pieces/b").Count);
        Assert.Equal(8, Regex.Matches(html, "/pieces/wP.svg").Count); // 8 белых пешек
    }

    [Fact]
    public void PromotionPickerShowsCburnettPiecesOfMovingColor()
    {
        // Белая пешка на e7, поле e8 пустое — клик e7→e8 должен открыть выбор фигуры превращения.
        const string promoFen = "4k3/4P3/8/8/8/8/8/4K3 w - - 0 1";
        var cut = Render<ChessSchool.Arena.Components.Chessboard>(p => p.Add(c => c.Fen, promoFen));

        // Перевыбираем клетки перед каждым кликом: после выбора пешки доска перерисовывается
        // и прежняя коллекция кнопок становится недействительной.
        cut.FindAll("button.sq")[12].Click(); // e7 (row 1, col 4) — выбираем пешку
        cut.FindAll("button.sq")[4].Click();  // e8 (row 0, col 4) — целевое поле превращения

        var picker = cut.FindAll(".promo-btn");
        Assert.Equal(4, picker.Count); // ферзь/ладья/слон/конь — есть недопревращение, не только ферзь

        var html = cut.Markup;
        // Картинки Cburnett именно белого цвета (ходит белая пешка), а не юникод-глифы.
        Assert.Contains("/pieces/wQ.svg", html);
        Assert.Contains("/pieces/wR.svg", html);
        Assert.Contains("/pieces/wB.svg", html);
        Assert.Contains("/pieces/wN.svg", html);
        Assert.DoesNotContain("♛", html);
    }

    [Fact]
    public void HighlightsLastMoveAndCheck()
    {
        var cut = Render<ChessSchool.Arena.Components.Chessboard>(p => p
            .Add(c => c.Fen, StartFen)
            .Add(c => c.LastFrom, "e2")
            .Add(c => c.LastTo, "e4")
            .Add(c => c.CheckSquare, "e1"));

        var html = cut.Markup;
        // Считаем класс только на кнопках-клетках (в CSS-правилах внутри <style> слова те же).
        Assert.Equal(2, Regex.Matches(html, "class=\"sq[^\"]*lastmove").Count); // e2 и e4
        Assert.Equal(1, Regex.Matches(html, "class=\"sq[^\"]*check").Count);    // e1 (король под шахом)
    }
}
