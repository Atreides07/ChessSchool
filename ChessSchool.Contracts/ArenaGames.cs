namespace ChessSchool.Contracts;

/// <summary>
/// Завершённая арена-партия для архива/разбора. Передаётся Arena→ApiService по HTTP (источник истины —
/// Postgres). Полный PGN нужен, чтобы воспроизвести партию и посчитать разбор Stockfish позже.
/// </summary>
public sealed record ArenaGameArchiveRequest(
    string TournamentId,
    string GameId,
    string WhiteSub,
    string BlackSub,
    string WhiteName,
    string BlackName,
    bool WhiteIsBot,
    bool BlackIsBot,
    string Pgn,
    GameResult Result,
    GameEndReason EndReason,
    TimeControl TimeControl,
    DateTimeOffset PlayedAt);

/// <summary>Строка списка «Мои партии» (с точки зрения текущего игрока).</summary>
public sealed record ArenaGameListItem(
    Guid Id,
    string TournamentId,
    string OpponentName,
    bool OpponentIsBot,
    PieceColor MyColor,
    PlayerOutcome Outcome,
    GameEndReason EndReason,
    TimeControl TimeControl,
    DateTimeOffset PlayedAt,
    bool Analyzed);

/// <summary>Страница истории партий игрока.</summary>
public sealed record ArenaGameListPage(
    IReadOnlyList<ArenaGameListItem> Items,
    int Total);

/// <summary>Партия для воспроизведения/разбора (полные данные обеих сторон).</summary>
public sealed record ArenaGameDetail(
    Guid Id,
    string TournamentId,
    string WhiteName,
    string BlackName,
    bool WhiteIsBot,
    bool BlackIsBot,
    string Pgn,
    GameResult Result,
    GameEndReason EndReason,
    TimeControl TimeControl,
    DateTimeOffset PlayedAt,
    PieceColor MyColor);

/// <summary>Исход партии с точки зрения игрока.</summary>
public enum PlayerOutcome { Win = 0, Loss = 1, Draw = 2 }

/// <summary>Качество хода по потере оценки относительно лучшего (lichess-подобно).</summary>
public enum MoveQuality { Best = 0, Good = 1, Inaccuracy = 2, Mistake = 3, Blunder = 4 }

/// <summary>Разбор одного полухода: оценка после хода (в сантипешках, со стороны белых), классификация.</summary>
public sealed record MoveAnalysisDto(
    int Ply,
    string San,
    PieceColor Side,
    int ScoreCp,        // оценка позиции ПОСЛЕ хода, с точки зрения белых (мат конвертируется в ±крупное)
    int? MateIn,        // мат в N (знак: + за белых), либо null
    MoveQuality Quality,
    string? BestFrom,   // лучший ход в позиции ДО (для стрелки на доске), координаты, либо null
    string? BestTo);

/// <summary>Полный разбор партии: оценки по ходам + точность каждой стороны.</summary>
public sealed record GameAnalysisDto(
    double WhiteAccuracy,
    double BlackAccuracy,
    int WhiteInaccuracies,
    int WhiteMistakes,
    int WhiteBlunders,
    int BlackInaccuracies,
    int BlackMistakes,
    int BlackBlunders,
    IReadOnlyList<MoveAnalysisDto> Moves,
    bool EngineAvailable);
