using ChessSchool.Contracts;

namespace ChessSchool.Arena.Grains;

/// <summary>
/// Долговечная часть турнира (мета + таблица). Сохраняется в grain storage, поэтому переживает
/// деактивацию грейна: при повторной активации очки/серии/история партий восстанавливаются.
/// Активные партии (доски) намеренно НЕ сохраняем — при реактивации простаивающие игроки
/// мгновенно переспариваются, прерванная партия начинается заново (потеря одной партии терпима).
/// </summary>
[GenerateSerializer]
public sealed class ArenaPersistedState
{
    [Id(0)] public bool Configured { get; set; }
    [Id(1)] public bool FinishedDemo { get; set; }
    [Id(2)] public string Name { get; set; } = "";
    [Id(3)] public TimeControl Tc { get; set; } = new(180, 0);
    [Id(4)] public int DurationSeconds { get; set; }
    [Id(5)] public DateTimeOffset StartsAt { get; set; }
    [Id(6)] public int BotCounter { get; set; }
    [Id(7)] public List<PersistedPlayer> Players { get; set; } = [];
}

[GenerateSerializer]
public sealed class PersistedPlayer
{
    [Id(0)] public string Key { get; set; } = "";
    [Id(1)] public string Name { get; set; } = "";
    [Id(2)] public bool IsBot { get; set; }
    [Id(3)] public int Score { get; set; }
    [Id(4)] public int Streak { get; set; }
    [Id(5)] public int Games { get; set; }
    [Id(6)] public int Wins { get; set; }
    [Id(7)] public List<int> Results { get; set; } = [];
}
