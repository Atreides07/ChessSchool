using Chess;

namespace ChessSchool.Tests;

/// <summary>
/// Закрепляет контракт библиотеки Gera.Chess, на который опирается GameGrain
/// (Turn.AsChar, IsValidMove/Move, IsEndGame, EndGame.WonSide, EndgameType).
/// </summary>
public class ChessRulesTests
{
    [Fact]
    public void NewBoard_WhiteToMove()
    {
        var board = new ChessBoard();
        Assert.Equal('w', board.Turn.AsChar);
    }

    [Fact]
    public void IllegalMove_IsRejected()
    {
        var board = new ChessBoard();
        Assert.False(board.IsValidMove(new Move("e2", "e5")));
    }

    [Fact]
    public void ScholarsMate_IsCheckmate_WhiteWins()
    {
        var board = new ChessBoard();
        string[,] moves =
        {
            { "e2", "e4" }, { "e7", "e5" },
            { "f1", "c4" }, { "b8", "c6" },
            { "d1", "h5" }, { "g8", "f6" },
            { "h5", "f7" }
        };

        for (int i = 0; i < moves.GetLength(0); i++)
        {
            var move = new Move(moves[i, 0], moves[i, 1]);
            Assert.True(board.IsValidMove(move), $"Ход {moves[i, 0]}{moves[i, 1]} должен быть легальным");
            board.Move(move);
        }

        Assert.True(board.IsEndGame);
        Assert.Equal('w', board.EndGame!.WonSide!.AsChar);
        Assert.Equal("Checkmate", board.EndGame.EndgameType.ToString());
    }
}
