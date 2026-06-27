using System.Collections.Concurrent;
using ChessSchool.Arena.Grains;
using ChessSchool.Arena.Hubs;
using ChessSchool.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Мост «событие грейна → группа SignalR». При первом зрителе турнира на ноде (<see cref="Watch"/>)
/// подписывается на <see cref="ArenaNotifier"/> по id; на каждое уведомление читает ОБЩЕЕ состояние
/// турнира (один раз) и рассылает его ЛОКАЛЬНЫМ участникам группы.
///
/// SignalR-backplane намеренно НЕ используется: <see cref="ArenaNotifier"/> уже кросс-нодовый (Redis
/// pub/sub), поэтому уведомление приходит на каждую ноду, и каждая обслуживает свои локальные соединения —
/// полное покрытие без дублей и без второго фан-аута. Персональная часть (своя партия игрока) приходит
/// клиенту отдельно — по его собственному вызову GetState (игроков мало, зрителей много → масштабируется).
/// Подписка снимается, когда турнир завершился (грейн перестаёт уведомлять).
/// </summary>
public sealed class ArenaBroadcaster(
    IGrainFactory grains,
    IHubContext<ArenaHub> hub,
    ArenaNotifier notifier,
    ILogger<ArenaBroadcaster> log) : IDisposable
{
    private readonly ConcurrentDictionary<string, IDisposable> _subs = new();

    /// <summary>Гарантирует подписку на уведомления турнира (идемпотентно, одна на id на ноду).</summary>
    public void Watch(string id) =>
        _subs.GetOrAdd(id, key => notifier.Subscribe(key, () => _ = PushAsync(key)));

    private async Task PushAsync(string id)
    {
        try
        {
            // Общий вид (пустой sub → без персональной партии) — одно чтение грейна на всю группу.
            var shared = await grains.GetGrain<IArenaTournamentGrain>(id).GetStateAsync("");
            await hub.Clients.Group(id).SendAsync("ArenaState", shared);

            // Турнир завершился — грейн больше не уведомляет, снимаем подписку (не копим по нодам).
            if (shared.Status == TournamentStatus.Finished && _subs.TryRemove(id, out var sub))
                sub.Dispose();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Не удалось разослать состояние турнира {Id}.", id);
        }
    }

    public void Dispose()
    {
        foreach (var s in _subs.Values) s.Dispose();
        _subs.Clear();
    }
}
