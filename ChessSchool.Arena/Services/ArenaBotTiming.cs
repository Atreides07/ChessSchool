namespace ChessSchool.Arena.Services;

/// <summary>
/// Время «обдумывания» хода ботом — чтобы ходы были неравномерными, «человеческими», а не по метроному.
/// Чистая функция: всю случайность (роллы) передаёт грейн (Random.Shared), поэтому поведение тестируемо.
/// </summary>
public static class ArenaBotTiming
{
    /// <param name="forcedRoll">ролл [0,1) для вынужденного хода.</param>
    /// <param name="jitter">ролл [0,1) «человеческого» разброса.</param>
    public static int ThinkMs(int legalMoveCount, bool inCheck, long myMs, double speedFactor, double forcedRoll, double jitter)
    {
        // Единственный/вынужденный ход — почти мгновенно (80..199 мс).
        if (legalMoveCount <= 1) return 80 + (int)(forcedRoll * 120);

        // Бюджет ~ оставшееся время на ~25 предстоящих ходов: под нехватку времени бот ускоряется.
        double budget = Math.Max(150, myMs / 25.0);
        // Сложность: шах и обилие вариантов → дольше; мало вариантов → быстро.
        double complexity = inCheck ? 0.9 : Math.Min(1.0, 0.25 + legalMoveCount * 0.02);
        // Личность: слабые ходят быстрее, сильные обстоятельнее; плюс джиттер.
        double t = budget * complexity * speedFactor * (0.7 + jitter * 0.6);
        return (int)Math.Clamp(t, 90, 2500);
    }
}
