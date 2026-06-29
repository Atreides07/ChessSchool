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
    [property: Id(5)] int SecondsLeft,
    [property: Id(6)] int BotCount,
    [property: Id(7)] DateTimeOffset StartsAt,
    [property: Id(8)] int DurationSeconds,
    [property: Id(9)] bool Joined = false)
{
    /// <summary>Участники-люди = все минус боты.</summary>
    public int HumanCount => Math.Max(0, PlayerCount - BotCount);
}

/// <summary>Строка таблицы лидеров арены.</summary>
[GenerateSerializer]
public sealed record ArenaStandingRow(
    [property: Id(0)] int Rank,
    [property: Id(1)] string Name,
    [property: Id(2)] int Score,
    [property: Id(3)] int Streak,
    [property: Id(4)] bool OnFire,
    [property: Id(5)] bool Playing,
    [property: Id(6)] int Games,
    [property: Id(7)] int Wins,
    [property: Id(8)] IReadOnlyList<int> Results,
    [property: Id(9)] bool IsBot = false);

/// <summary>Партия для трансляции «идёт сейчас» (ориентация доски — белые снизу, очки турнира у имён).</summary>
[GenerateSerializer]
public sealed record ArenaBoardDto(
    [property: Id(0)] string GameId,
    [property: Id(1)] string Fen,
    [property: Id(2)] string WhiteName,
    [property: Id(3)] string BlackName,
    [property: Id(4)] int WhiteScore,
    [property: Id(5)] int BlackScore,
    [property: Id(6)] long WhiteMs,
    [property: Id(7)] long BlackMs,
    [property: Id(8)] PieceColor Turn,
    [property: Id(9)] GameStatus Status,
    [property: Id(10)] GameResult Result,
    [property: Id(11)] string? LastFrom = null,
    [property: Id(12)] string? LastTo = null,
    [property: Id(13)] string? CheckSquare = null,
    [property: Id(14)] bool WhiteIsBot = false,
    [property: Id(15)] bool BlackIsBot = false);

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
    [property: Id(16)] string? CheckSquare = null,
    [property: Id(17)] bool WhiteIsBot = false,
    [property: Id(18)] bool BlackIsBot = false,
    [property: Id(19)] bool DrawOfferFromOpponent = false,
    [property: Id(20)] bool DrawOfferByMe = false);

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
    [property: Id(7)] ArenaGameDto? MyGame,
    [property: Id(8)] TimeControl TimeControl,
    [property: Id(9)] DateTimeOffset StartedAt,
    [property: Id(10)] int DurationSeconds,
    [property: Id(11)] int BotCount,
    [property: Id(12)] IReadOnlyList<ArenaBoardDto> Boards,
    // Игрок нажал «подобрать соперника» и ждёт пары (но партия ещё не началась). Подбор НЕ автоматический:
    // до нажатия игрок просто записан и не ищет соперника. false для зрителей/анонимов.
    [property: Id(13)] bool Seeking = false);

/// <summary>Админ-операция: добавить предложенный (найденный во внешнем источнике) турнир в каталог трансляций по его slug.</summary>
public sealed record AddSuggestedTournamentRequest(string Slug);
