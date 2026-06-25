using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

/// <summary>Проверяет механизм ходов бота (случайный легальный ход).</summary>
public class ArenaBotTests
{
    [Fact]
    public void RandomMove_IsLegal_AndFlipsTurn()
    {
        var game = new ChessGame();
        var before = game.Turn;

        Assert.True(game.TryMakeRandomMove());
        Assert.NotEqual(before, game.Turn);   // очередь хода сменилась
        Assert.NotNull(game.LastSan);          // ход записан в нотации
    }

    [Fact]
    public void Bot_PlaysSequenceOfLegalMoves()
    {
        var game = new ChessGame();
        int moves = 0;
        for (int i = 0; i < 30 && !game.IsEndGame; i++)
        {
            Assert.True(game.TryMakeRandomMove(), "Случайный ход всегда должен быть легальным.");
            moves++;
        }
        Assert.True(moves > 0);
    }
}
