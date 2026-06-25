namespace ChessSchool.Contracts;

public enum TournamentStatus
{
    Created = 0,
    Running = 1,
    Finished = 2
}

/// <summary>Карточка турнира в списке (как плитки на lichess/tournament).</summary>
[GenerateSerializer]
public sealed record TournamentSummaryDto(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] TimeControl TimeControl,
    [property: Id(3)] TournamentStatus Status,
    [property: Id(4)] int PlayerCount,
    [property: Id(5)] int SecondsLeft);

/// <summary>Строка таблицы лидеров арены.</summary>
[GenerateSerializer]
public sealed record ArenaStandingRow(
    [property: Id(0)] int Rank,
    [property: Id(1)] string Name,
    [property: Id(2)] int Score,
    [property: Id(3)] int Streak,
    [property: Id(4)] bool OnFire,
    [property: Id(5)] bool Playing);

/// <summary>Текущая партия игрока внутри турнира.</summary>
[GenerateSerializer]
public sealed record ArenaGameDto(
    [property: Id(0)] string GameId,
    [property: Id(1)] string Fen,
    [property: Id(2)] PieceColor MyColor,
    [property: Id(3)] PieceColor Turn,
    [property: Id(4)] string WhiteName,
    [property: Id(5)] string BlackName,
    [property: Id(6)] long WhiteMs,
    [property: Id(7)] long BlackMs,
    [property: Id(8)] GameStatus Status,
    [property: Id(9)] GameResult Result,
    [property: Id(10)] string? LastMoveSan,
    [property: Id(11)] bool WhiteBerserk,
    [property: Id(12)] bool BlackBerserk,
    [property: Id(13)] bool MyBerserkAvailable,
    [property: Id(14)] string? LastFrom = null,
    [property: Id(15)] string? LastTo = null,
    [property: Id(16)] string? CheckSquare = null);

/// <summary>Полное состояние турнира для игрока (доска лидеров + его партия).</summary>
[GenerateSerializer]
public sealed record ArenaStateDto(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] TournamentStatus Status,
    [property: Id(3)] int SecondsLeft,
    [property: Id(4)] bool Joined,
    [property: Id(5)] int MyScore,
    [property: Id(6)] IReadOnlyList<ArenaStandingRow> Standings,
    [property: Id(7)] ArenaGameDto? MyGame);
