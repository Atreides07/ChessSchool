namespace ChessSchool.Arena.Services;

/// <summary>
/// Подсчёт очков арены со «стриком» (как на lichess): после 2 побед подряд игрок «на огне» —
/// победа даёт 4 очка, ничья 2; иначе победа 2, ничья 1. Поражение обнуляет стрик.
/// </summary>
public static class ArenaScoring
{
    /// <param name="outcome">1.0 — победа, 0.5 — ничья, 0.0 — поражение.</param>
    public static (int Score, int Streak) Apply(int score, int streak, double outcome)
    {
        bool onFire = streak >= 2;
        return outcome switch
        {
            1.0 => (score + (onFire ? 4 : 2), streak + 1),
            0.5 => (score + (onFire ? 2 : 1), streak),
            _ => (score, 0)
        };
    }
}
