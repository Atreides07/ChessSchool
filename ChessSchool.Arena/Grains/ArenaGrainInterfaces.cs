using ChessSchool.Contracts;

namespace ChessSchool.Arena.Grains;

/// <summary>Каталог турниров (синглтон, ключ 0). Генерирует расписание и отдаёт список.</summary>
public interface IArenaDirectoryGrain : IGrainWithIntegerKey
{
    Task<IReadOnlyList<TournamentSummaryDto>> ListAsync(string? sub = null);
}

/// <summary>
/// Грейн одного арена-турнира (ключ = уникальный id). Жизненный цикл по времени:
/// Created (регистрация) → Running (непрерывный пейринг, игра) → Finished (результаты).
/// Боты добираются до минимума участников и сокращаются по мере прихода людей (как на lichess).
/// </summary>
public interface IArenaTournamentGrain : IGrainWithStringKey
{
    Task ConfigureAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds);
    Task ConfigureFinishedDemoAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds);
    /// <summary>Конфигурация бренд-турнира из каталога админки (можно переконфигурировать до старта).</summary>
    Task ConfigureBrandAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds);
    Task JoinAsync(string sub, string name);
    /// <summary>Игрок нажал «подобрать соперника» — войти в пул подбора (подбор не автоматический).</summary>
    Task SeekAsync(string sub);
    Task<ArenaStateDto> GetStateAsync(string sub);
    Task<TournamentSummaryDto> GetSummaryAsync(string? sub = null);
    Task<TournamentSummaryDto> PeekSummaryAsync(string? sub = null);
    Task<IReadOnlyList<ArenaBoardDto>> GetBoardsAsync();
    Task<ArenaGameDto?> MoveAsync(string sub, MoveInput move);
    Task BerserkAsync(string sub);
    Task ResignAsync(string sub);
    /// <summary>Предложить ничью. Возвращает исход: "accepted" / "declined" (бот) / "offered" (ждём соперника) / "".</summary>
    Task<string> OfferDrawAsync(string sub);
    /// <summary>Принять предложение ничьи соперника.</summary>
    Task AcceptDrawAsync(string sub);
    /// <summary>Отклонить предложение ничьи соперника.</summary>
    Task DeclineDrawAsync(string sub);
}
