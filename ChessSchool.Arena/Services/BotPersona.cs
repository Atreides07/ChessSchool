namespace ChessSchool.Arena.Services;

/// <summary>
/// «Личность» бота арены: рейтинг (для отображения), уровень Stockfish (сила игры) и множитель времени
/// на обдумывание (слабые игроки ходят быстрее, сильные — обстоятельнее). Привязка к конкретному боту
/// детерминирована его ключом → один и тот же бот всегда одной силы и переживает реактивацию грейна
/// (личность не хранится отдельно, а восстанавливается из ключа).
/// </summary>
public readonly record struct BotPersona(int Rating, int Skill, double Speed)
{
    // Разброс по силе: от слабого новичка до сильного движка. Skill — уровень Stockfish (0..20),
    // Speed — множитель времени на ход (см. ArenaTournamentGrain.BotThinkMs).
    private static readonly BotPersona[] Roster =
    [
        new(1000, 1, 0.55),
        new(1250, 3, 0.65),
        new(1500, 6, 0.80),
        new(1700, 9, 0.90),
        new(1900, 12, 1.00),
        new(2100, 16, 1.10),
        new(2300, 20, 1.20),
    ];

    /// <summary>Детерминированно сопоставляет боту личность по его ключу.</summary>
    public static BotPersona For(string key)
    {
        int h = 0;
        foreach (var ch in key) h = unchecked(h * 31 + ch) & 0x7fffffff;
        return Roster[h % Roster.Length];
    }
}
