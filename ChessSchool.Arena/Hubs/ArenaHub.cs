using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ChessSchool.Arena.Hubs;

/// <summary>
/// Транспортный ярус страницы турнира (тонкий клиент). Держит WebSocket-соединения зрителей/игроков
/// и проксирует действия в Orleans-грейн турнира; состояние и логика — в грейне. Рассылку обновлений
/// в группу делает <see cref="ArenaBroadcaster"/> по событию грейна (ArenaNotifier).
///
/// Аутентификация — по cookie сессии (хаб того же origin, что и страница, браузер шлёт cookie на
/// WS-рукопожатии). Зрители без входа допускаются (только просмотр); действия требуют пользователя.
/// </summary>
public sealed class ArenaHub(IGrainFactory grains, ArenaBroadcaster broadcaster) : Hub
{
    private string? Sub => Context.User?.FindFirst("sub")?.Value;
    private string Name => Context.User?.FindFirst("name")?.Value ?? Context.User?.Identity?.Name ?? "Игрок";

    private IArenaTournamentGrain Grain(string id) => grains.GetGrain<IArenaTournamentGrain>(id);

    private string RequireSub() => Sub ?? throw new HubException("Требуется вход.");

    /// <summary>Подписаться на турнир: вступить в группу рассылки и получить начальное состояние.</summary>
    public async Task<ArenaStateDto> JoinTournament(string id)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, id);
        broadcaster.Watch(id); // обеспечить ретрансляцию обновлений грейна в группу на этой ноде
        return await Grain(id).GetStateAsync(Sub ?? "");
    }

    /// <summary>Персональное состояние (ресинк после reconnect / для участника после общего пуша).</summary>
    public Task<ArenaStateDto> GetState(string id) => Grain(id).GetStateAsync(Sub ?? "");

    /// <summary>
    /// Страница «Все игры» (тонкий клиент): вступить в группу рассылки и получить ПОЛНЫЙ список досок.
    /// В отличие от <see cref="JoinTournament"/> (4 доски для шапки) отдаёт все партии турнира.
    /// </summary>
    public async Task<IReadOnlyList<ArenaBoardDto>> JoinAllGames(string id)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, id);
        broadcaster.Watch(id); // ретрансляция обновлений грейна в группу на этой ноде
        return await Grain(id).GetBoardsAsync();
    }

    /// <summary>Перезабор полного списка досок (по общему пушу/после reconnect на странице «Все игры»).</summary>
    public Task<IReadOnlyList<ArenaBoardDto>> GetAllBoards(string id) => Grain(id).GetBoardsAsync();

    /// <summary>Записаться/присоединиться к турниру (требует входа).</summary>
    public async Task<ArenaStateDto> Register(string id)
    {
        var sub = RequireSub();
        await Grain(id).JoinAsync(sub, Name);
        return await Grain(id).GetStateAsync(sub);
    }

    public async Task<ArenaStateDto> Move(string id, string from, string to, string? promotion)
    {
        var sub = RequireSub();
        await Grain(id).MoveAsync(sub, new MoveInput(from, to, promotion));
        return await Grain(id).GetStateAsync(sub);
    }

    /// <summary>Нажата кнопка «подобрать соперника» — войти в пул подбора (требует входа).</summary>
    public async Task<ArenaStateDto> SeekOpponent(string id)
    {
        var sub = RequireSub();
        await Grain(id).SeekAsync(sub);
        return await Grain(id).GetStateAsync(sub);
    }

    public async Task<ArenaStateDto> Berserk(string id)
    {
        var sub = RequireSub();
        await Grain(id).BerserkAsync(sub);
        return await Grain(id).GetStateAsync(sub);
    }

    public async Task<ArenaStateDto> Resign(string id)
    {
        var sub = RequireSub();
        await Grain(id).ResignAsync(sub);
        return await Grain(id).GetStateAsync(sub);
    }
}
