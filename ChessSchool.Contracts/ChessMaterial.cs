namespace ChessSchool.Contracts;

/// <summary>
/// Достаточно ли стороне материала, чтобы в принципе поставить мат. Нужно для правила
/// «просрочка времени против недостаточного материала = ничья» (FIDE 6.9 / lichess): если у
/// соперника просрочившего время игрока нет материала на мат, партия не выигрывается по времени,
/// а завершается вничью.
///
/// «Достаточно», если есть хотя бы пешка/ладья/ферзь или ≥2 лёгкие фигуры (конь/слон). Не хватает:
/// одинокий король, король+конь, король+слон (K, K+N, K+B). K+N+N считаем достаточным (мат возможен,
/// хоть и не форсируется) — как на lichess. Считается по FEN, поэтому не зависит от шахматной библиотеки.
/// </summary>
public static class ChessMaterial
{
    /// <param name="fen">FEN позиции (используется поле расстановки фигур).</param>
    /// <param name="white">true — считать материал белых, false — чёрных.</param>
    public static bool HasMatingMaterial(string fen, bool white)
    {
        if (string.IsNullOrWhiteSpace(fen)) return true; // нет данных — не присуждаем ничью по ошибке

        var placement = fen.Split(' ')[0];
        int pawns = 0, rooks = 0, queens = 0, minors = 0;
        foreach (var ch in placement)
        {
            if (!char.IsLetter(ch)) continue;
            if (char.IsUpper(ch) != white) continue; // фигуры нужной стороны (верхний регистр — белые)
            switch (char.ToUpperInvariant(ch))
            {
                case 'P': pawns++; break;
                case 'R': rooks++; break;
                case 'Q': queens++; break;
                case 'N':
                case 'B': minors++; break;
                    // 'K' — короля не учитываем
            }
        }

        return pawns > 0 || rooks > 0 || queens > 0 || minors >= 2;
    }
}
