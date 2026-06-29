namespace ChessSchool.Arena.Services;

/// <summary>
/// Решение бота по предложению ничьи. Чистые функции: оценку движка (<see cref="EngineEval"/>) добывает
/// грейн, а перспективу и пороги считаем здесь (тестируемо). Бот соглашается, если НЕ выигрывает явно.
/// </summary>
public static class ArenaBotDraw
{
    /// <summary>
    /// Оценка в сантипешках с точки зрения бота. Движок даёт оценку со стороны игрока, чей ход (side-to-move),
    /// поэтому при ходе соперника знак инвертируется. Мат трактуем как ±100000.
    /// </summary>
    public static int BotCp(EngineEval eval, bool botIsWhite, bool whiteToMove)
    {
        int stmCp = eval.Mate is int m ? (m > 0 ? 100000 : -100000) : (eval.Cp ?? 0);
        bool botToMove = whiteToMove == botIsWhite;
        return botToMove ? stmCp : -stmCp;
    }

    /// <summary>Бот согласен на ничью: заметно хуже (≤ -150) — всегда; равная позиция (≤ +20) вне дебюта (ход ≥ 10).</summary>
    public static bool ShouldAccept(int botCp, int fullmove) =>
        botCp <= -150 || (botCp <= 20 && fullmove >= 10);

    /// <summary>Номер полного хода из FEN (поле 6); по умолчанию 1.</summary>
    public static int FullmoveFromFen(string fen)
    {
        var parts = fen.Split(' ');
        return parts.Length >= 6 && int.TryParse(parts[5], out var n) ? n : 1;
    }
}
