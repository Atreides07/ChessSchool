namespace ChessSchool.Contracts;

/// <summary>Контроль времени партии (например, 300+2 — пять минут плюс 2 секунды на ход).</summary>
[GenerateSerializer]
public sealed record TimeControl([property: Id(0)] int InitialSeconds, [property: Id(1)] int IncrementSeconds)
{
    public static readonly TimeControl Blitz = new(300, 2);
    public static readonly TimeControl Rapid = new(600, 5);
    public static readonly TimeControl Bullet = new(60, 1);

    public override string ToString() => $"{InitialSeconds / 60}+{IncrementSeconds}";
}

/// <summary>Запрос на поиск соперника (матчмейкинг).</summary>
[GenerateSerializer]
public sealed record MatchRequest(
    [property: Id(0)] string UserId,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] int Rating,
    [property: Id(3)] TimeControl TimeControl);

/// <summary>Уведомление игроку о найденной паре.</summary>
[GenerateSerializer]
public sealed record MatchFound(
    [property: Id(0)] string GameId,
    [property: Id(1)] PieceColor Color,
    [property: Id(2)] string OpponentId,
    [property: Id(3)] string OpponentName,
    [property: Id(4)] int OpponentRating,
    [property: Id(5)] TimeControl TimeControl);

/// <summary>Ход игрока в координатной нотации (e2-e4), с опциональным превращением.</summary>
[GenerateSerializer]
public sealed record MoveInput(
    [property: Id(0)] string From,
    [property: Id(1)] string To,
    [property: Id(2)] string? Promotion = null);

/// <summary>
/// Полное состояние партии, рассылаемое подключённым клиентам.
/// FEN — единый источник истины о позиции; часы в миллисекундах.
/// </summary>
[GenerateSerializer]
public sealed record GameStateDto(
    [property: Id(0)] string GameId,
    [property: Id(1)] string Fen,
    [property: Id(2)] string WhitePlayerId,
    [property: Id(3)] string WhitePlayerName,
    [property: Id(4)] string BlackPlayerId,
    [property: Id(5)] string BlackPlayerName,
    [property: Id(6)] PieceColor Turn,
    [property: Id(7)] long WhiteMs,
    [property: Id(8)] long BlackMs,
    [property: Id(9)] string? LastMoveSan,
    [property: Id(10)] GameStatus Status,
    [property: Id(11)] GameResult Result,
    [property: Id(12)] GameEndReason EndReason,
    [property: Id(13)] int MoveNumber,
    [property: Id(14)] string? LastFrom = null,
    [property: Id(15)] string? LastTo = null,
    [property: Id(16)] string? CheckSquare = null);

/// <summary>Результат попытки сделать ход.</summary>
[GenerateSerializer]
public sealed record MoveResult(
    [property: Id(0)] bool Accepted,
    [property: Id(1)] string? Error,
    [property: Id(2)] GameStateDto? State);
