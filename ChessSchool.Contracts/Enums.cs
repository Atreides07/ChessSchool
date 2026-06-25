namespace ChessSchool.Contracts;

/// <summary>Цвет игрока за доской.</summary>
public enum PieceColor
{
    White = 0,
    Black = 1
}

/// <summary>Текущий статус партии.</summary>
public enum GameStatus
{
    WaitingForOpponent = 0,
    InProgress = 1,
    Finished = 2,
    Aborted = 3
}

/// <summary>Итог завершённой партии.</summary>
public enum GameResult
{
    Ongoing = 0,
    WhiteWins = 1,
    BlackWins = 2,
    Draw = 3,
    Aborted = 4
}

/// <summary>Причина завершения партии.</summary>
public enum GameEndReason
{
    None = 0,
    Checkmate = 1,
    Resignation = 2,
    Timeout = 3,
    Stalemate = 4,
    DrawAgreed = 5,
    InsufficientMaterial = 6,
    Abandoned = 7
}

/// <summary>Тип соперника для атрибуции тренировочной партии.</summary>
public enum OpponentType
{
    Student = 0,
    Guest = 1,
    Coach = 2
}

/// <summary>Источник привязки партии к ученикам.</summary>
public enum AttributionSource
{
    None = 0,
    OnlineMatch = 1,
    CheckIn = 2,
    Manual = 3,
    Tournament = 4
}
