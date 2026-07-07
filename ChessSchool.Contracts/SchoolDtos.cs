namespace ChessSchool.Contracts;

/// <summary>Школа.</summary>
public sealed record SchoolDto(Guid Id, string Name);

/// <summary>Учебная группа внутри школы.</summary>
public sealed record GroupDto(Guid Id, Guid SchoolId, string Name);

/// <summary>Создание учебной группы.</summary>
public sealed record CreateGroupRequest(string Name);

/// <summary>Перевод ученика в другую группу (в пределах его школы).</summary>
public sealed record MoveStudentRequest(Guid GroupId);

/// <summary>Строка таблицы учеников в ЛК школы.</summary>
public sealed record StudentDto(
    Guid Id,
    Guid GroupId,
    string DisplayName,
    int Rating,
    int RatingDeviation,
    int GamesPlayed,
    int Wins,
    int Draws,
    int Losses,
    string? LinkedUserSub,
    DateOnly? BirthDate = null,
    int RecentDelta = 0)
{
    public double WinRate => GamesPlayed == 0 ? 0 : Math.Round(100.0 * Wins / GamesPlayed, 1);
    public bool AccountLinked => !string.IsNullOrEmpty(LinkedUserSub);
}

/// <summary>Привязка профиля ученика к онлайн-аккаунту по email.</summary>
public sealed record LinkAccountRequest(string Email);

/// <summary>Точка истории рейтинга для графика.</summary>
public sealed record RatingPointDto(DateTimeOffset Date, int Rating);

/// <summary>Краткая карточка сыгранной партии.</summary>
public sealed record GameSummaryDto(
    Guid Id,
    DateTimeOffset PlayedAt,
    string OpponentName,
    PieceColor Color,
    GameResult Result,
    int RatingChange,
    string? Pgn);

/// <summary>Полный профиль ученика (для тренера и публичного шаринга родителю).</summary>
public sealed record StudentProfileDto(
    StudentDto Student,
    IReadOnlyList<RatingPointDto> RatingHistory,
    IReadOnlyList<GameSummaryDto> RecentGames);

/// <summary>Запрос на создание ученика.</summary>
public sealed record CreateStudentRequest(Guid GroupId, string DisplayName, DateOnly? BirthDate);

/// <summary>Запрос на редактирование ученика (имя/дата рождения).</summary>
public sealed record UpdateStudentRequest(string DisplayName, DateOnly? BirthDate);

/// <summary>Ученик с изменением рейтинга за период (для «вырос/просел» на дашборде тренера).</summary>
public sealed record InsightStudentDto(Guid Id, string Name, int Delta);

/// <summary>Неактивный ученик (давно не играл) для дашборда тренера.</summary>
public sealed record InactiveStudentDto(Guid Id, string Name, int? DaysSinceLastGame);

/// <summary>Сводка для тренера за неделю: кто вырос/просел, кто не играл, активность.</summary>
public sealed record SchoolInsightsDto(
    IReadOnlyList<InsightStudentDto> MostImproved,
    IReadOnlyList<InsightStudentDto> Declined,
    IReadOnlyList<InactiveStudentDto> Inactive,
    int ActiveThisWeek,
    int GamesThisWeek,
    int TotalStudents);

/// <summary>Партия в очереди на атрибуцию (тренировочный сценарий).</summary>
public sealed record PendingGameDto(Guid Id, DateTimeOffset PlayedAt, string DeviceRef, string Pgn);

/// <summary>Назначение игроков и цветов на партию тренером.</summary>
public sealed record AttributeGameRequest(Guid WhiteStudentId, Guid BlackStudentId, GameResult Result);

/// <summary>Школа текущего пользователя (владельца) + её дефолтная группа — результат провижининга get-or-create.</summary>
public sealed record MySchoolDto(Guid SchoolId, Guid GroupId);

/// <summary>Ссылка на публичный профиль ученика для родителя.</summary>
public sealed record ShareLinkDto(string Token, string Url, DateTimeOffset? ExpiresAt);

/// <summary>Ссылка родителю с состоянием — для управления (список/отзыв) в ЛК.</summary>
public sealed record ShareLinkInfoDto(string Token, string Url, DateTimeOffset? ExpiresAt, bool Revoked)
{
    /// <summary>Действует ли ссылка сейчас (не отозвана и не просрочена).</summary>
    public bool Active => !Revoked && (ExpiresAt is null || ExpiresAt > DateTimeOffset.UtcNow);
}

/// <summary>
/// Запрос на архивацию завершённой онлайн-партии (GameServer → ApiService).
/// Игроки идентифицируются по их Sub из IdP; ApiService сам сопоставляет их с учениками.
/// </summary>
public sealed record ArchiveGameRequest(
    string GameId,
    string WhiteUserSub,
    string BlackUserSub,
    GameResult Result,
    GameEndReason EndReason,
    string Pgn,
    DateTimeOffset FinishedAt);
