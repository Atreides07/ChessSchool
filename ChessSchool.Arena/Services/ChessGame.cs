using Chess;
using ChessSchool.Contracts;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.Arena.Services;

/// <summary>Обёртка над Gera.Chess: валидация ходов, FEN, определение исхода. Используется грейном арены.</summary>
public sealed class ChessGame
{
    private readonly ChessBoard _board = new();
    private string? _pendingPromotion;

    public ChessGame()
    {
        _board.OnPromotePawn += (_, e) => e.PromotionResult = MapPromotion(_pendingPromotion);
    }

    public string Fen => _board.ToFen();
    public string Pgn => _board.ToPgn();
    public string? LastSan { get; private set; }
    public string? LastFrom { get; private set; }
    public string? LastTo { get; private set; }
    /// <summary>Клетка короля под шахом (для подсветки), либо null.</summary>
    public string? CheckSquare { get; private set; }
    public bool IsEndGame => _board.IsEndGame;
    public Color Turn => _board.Turn.AsChar == 'w' ? Color.White : Color.Black;

    /// <summary>Число легальных ходов в текущей позиции (оценка «сложности выбора» для тайминга бота).</summary>
    public int LegalMoveCount => _board.Moves().Length;
    /// <summary>Король стороны, чей ход, под шахом — выбор обычно вынужденный.</summary>
    public bool InCheck => _board.WhiteKingChecked || _board.BlackKingChecked;

    public bool TryMove(string from, string to, string? promotion)
    {
        _pendingPromotion = promotion;
        var move = new Move(from, to);
        if (!_board.IsValidMove(move)) return false;
        _board.Move(move);
        RecordMove(move);
        return true;
    }

    /// <summary>Ход бота: случайный легальный ход из текущей позиции.</summary>
    public bool TryMakeRandomMove()
    {
        var moves = _board.Moves();
        if (moves.Length == 0) return false;
        var move = moves[Random.Shared.Next(moves.Length)];
        _board.Move(move);
        RecordMove(move);
        return true;
    }

    private void RecordMove(Move move)
    {
        LastSan = move.San;
        LastFrom = Sq(move.OriginalPosition);
        LastTo = Sq(move.NewPosition);
        CheckSquare = _board.WhiteKingChecked ? Sq(_board.WhiteKing)
            : _board.BlackKingChecked ? Sq(_board.BlackKing)
            : null;
    }

    private static string Sq(Position p) => $"{(char)('a' + p.X)}{p.Y + 1}";

    public (GameResult Result, GameEndReason Reason) Resolve()
    {
        var eg = _board.EndGame;
        var result = eg?.WonSide is null ? GameResult.Draw
            : eg.WonSide.AsChar == 'w' ? GameResult.WhiteWins : GameResult.BlackWins;
        var reason = eg?.EndgameType.ToString() switch
        {
            "Checkmate" => GameEndReason.Checkmate,
            "Stalemate" => GameEndReason.Stalemate,
            "InsufficientMaterial" => GameEndReason.InsufficientMaterial,
            "Resigned" => GameEndReason.Resignation,
            _ => GameEndReason.DrawAgreed
        };
        return (result, reason);
    }

    private static PromotionType MapPromotion(string? p) => p?.ToLowerInvariant() switch
    {
        "r" => PromotionType.ToRook,
        "b" => PromotionType.ToBishop,
        "n" => PromotionType.ToKnight,
        _ => PromotionType.ToQueen
    };
}
