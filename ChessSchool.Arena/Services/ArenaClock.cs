using ChessSchool.Contracts;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Часы арена-партии: списание времени и разрешение исхода по просрочке. Чистые функции без состояния —
/// грейн владеет временем доски, а арифметику/правило недостатка материала держим здесь (тестируемо).
/// </summary>
public static class ArenaClock
{
    /// <summary>Списать <paramref name="elapsedMs"/> с часов. Возвращает новое значение и флаг просрочки (≤0 → 0, true).</summary>
    public static (long Ms, bool TimedOut) Deduct(long ms, long elapsedMs) =>
        ms - elapsedMs <= 0 ? (0L, true) : (ms - elapsedMs, false);

    /// <summary>
    /// Исход по просрочке времени стороной <paramref name="flagged"/>: поражение просрочившего; но если у
    /// соперника недостаточно материала для мата — ничья (FIDE 6.9 / lichess).
    /// </summary>
    public static (GameResult Result, GameEndReason Reason) ResolveTimeout(string fen, PieceColor flagged)
    {
        bool winnerIsWhite = flagged == PieceColor.Black;
        if (ChessMaterial.HasMatingMaterial(fen, winnerIsWhite))
            return (winnerIsWhite ? GameResult.WhiteWins : GameResult.BlackWins, GameEndReason.Timeout);
        return (GameResult.Draw, GameEndReason.InsufficientMaterial);
    }
}
