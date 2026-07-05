using ChessSchool.Contracts;

namespace ChessSchool.GameServer.Grains;

/// <summary>
/// Грейн одной партии. Ключ = gameId. Однопоточный доступ к состоянию (Orleans)
/// гарантирует отсутствие гонок без локов — основа масштабирования на миллионы партий.
/// </summary>
public interface IGameGrain : IGrainWithStringKey
{
    Task<GameStateDto> InitializeAsync(string whiteSub, string whiteName, string blackSub, string blackName, TimeControl tc);
    Task<MoveResult> TryMoveAsync(string playerSub, MoveInput move);
    Task<GameStateDto> ResignAsync(string playerSub);
    Task<GameStateDto?> GetStateAsync();
}

/// <summary>
/// Грейн матчмейкинга, по одному на контроль времени (ключ = "5+2").
/// Реентрантный: пока один игрок ждёт соперника, вызовы других игроков выполняются.
/// </summary>
public interface IMatchmakingGrain : IGrainWithStringKey
{
    /// <summary>Bounded long-poll: спарить с ждущим соперником ИЛИ подождать до WaitTimeout. Возвращает
    /// null, если за окно соперник не нашёлся (НЕ бросает) — клиент повторяет вызов (держит поиск).
    /// WaitTimeout ОБЯЗАН быть меньше response-timeout Orleans, иначе вызов пробьёт таймаут раньше.</summary>
    Task<MatchFound?> FindMatchAsync(MatchRequest request);
}
