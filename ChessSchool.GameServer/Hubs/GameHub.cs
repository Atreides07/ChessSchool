using ChessSchool.Contracts;
using ChessSchool.GameServer.Grains;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChessSchool.GameServer.Hubs;

/// <summary>
/// Транспортный ярус: держит WebSocket-соединения и проксирует действия в Orleans-грейны.
/// Логика и состояние партии — в грейнах; хаб лишь рассылает обновления в группу gameId.
/// </summary>
[Authorize]
public sealed class GameHub(IGrainFactory grains) : Hub
{
    private string Sub => Context.User?.FindFirst("sub")?.Value
        ?? throw new HubException("В токене нет sub.");
    private string Name => Context.User?.FindFirst("name")?.Value ?? "Игрок";

    /// <summary>Поиск соперника по контролю времени. Возвращается, когда пара найдена.</summary>
    public async Task<MatchFound> FindMatch(int initialSeconds, int increment)
    {
        var tc = new TimeControl(initialSeconds, increment);
        var mm = grains.GetGrain<IMatchmakingGrain>(tc.ToString());
        var found = await mm.FindMatchAsync(new MatchRequest(Sub, Name, 1200, tc));
        await Groups.AddToGroupAsync(Context.ConnectionId, found.GameId);
        return found;
    }

    /// <summary>Подключиться к существующей партии (реконнект/наблюдение).</summary>
    public async Task<GameStateDto?> JoinGame(string gameId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
        return await grains.GetGrain<IGameGrain>(gameId).GetStateAsync();
    }

    public async Task<MoveResult> Move(string gameId, string from, string to, string? promotion)
    {
        var result = await grains.GetGrain<IGameGrain>(gameId).TryMoveAsync(Sub, new MoveInput(from, to, promotion));
        if (result is { Accepted: true, State: not null })
            await Clients.Group(gameId).SendAsync("GameState", result.State);
        return result;
    }

    public async Task<GameStateDto> Resign(string gameId)
    {
        var state = await grains.GetGrain<IGameGrain>(gameId).ResignAsync(Sub);
        await Clients.Group(gameId).SendAsync("GameState", state);
        return state;
    }
}
