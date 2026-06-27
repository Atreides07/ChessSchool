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
    // Минимальный интервал между рассылками одного турнира. Уведомления приходят на каждый ход бота
    // (в булице — десятки в секунду); без коалесцинга это шквал чтений грейна + SignalR-сообщений +
    // перерисовок у зрителя → турнир «тормозит». Часы зритель анимирует локально, поэтому 4 пуша/сек
    // для ходов/результатов достаточно. Свой ход игрок видит сразу (отдельный GetState, не через рассылку).
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, IDisposable> _subs = new();
    private readonly ConcurrentDictionary<string, Coalescer> _gates = new();

    /// <summary>Гарантирует подписку на уведомления турнира (идемпотентно, одна на id на ноду).</summary>
    public void Watch(string id) =>
        _subs.GetOrAdd(id, key => notifier.Subscribe(key, () => Trigger(key)));

    // Коалесцируем шквал уведомлений: пока идёт рассылка/пауза, новые уведомления лишь помечают
    // «грязно», а не запускают параллельные рассылки. Так на турнир — не больше одной рассылки в MinInterval.
    private void Trigger(string id)
    {
        var gate = _gates.GetOrAdd(id, _ => new Coalescer());
        if (gate.Mark()) _ = RunLoopAsync(id, gate); // запускаем цикл, только если он ещё не идёт
    }

    private async Task RunLoopAsync(string id, Coalescer gate)
    {
        try
        {
            do
            {
                await PushAsync(id);
                await Task.Delay(MinInterval);
            }
            while (gate.ResetAndContinue()); // были ли новые уведомления за время рассылки/паузы
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Цикл рассылки турнира {Id} прерван.", id);
            gate.ForceStop();
        }
    }

    private async Task PushAsync(string id)
    {
        try
        {
            // Общий вид (пустой sub → без персональной партии) — одно чтение грейна на всю группу.
            var shared = await grains.GetGrain<IArenaTournamentGrain>(id).GetStateAsync("");
            await hub.Clients.Group(id).SendAsync("ArenaState", shared);

            // Турнир завершился — грейн больше не уведомляет, снимаем подписку (не копим по нодам).
            if (shared.Status == TournamentStatus.Finished && _subs.TryRemove(id, out var sub))
            {
                sub.Dispose();
                _gates.TryRemove(id, out _);
            }
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
        _gates.Clear();
    }

    /// <summary>
    /// Шлюз коалесцинга: пока рассылка «идёт», лишние уведомления лишь помечают «грязно».
    /// <see cref="Mark"/> возвращает true, если нужно ЗАПУСТИТЬ цикл (он ещё не запущен).
    /// <see cref="ResetAndContinue"/> в конце итерации: были новые уведомления → продолжаем, иначе стоп.
    /// </summary>
    private sealed class Coalescer
    {
        private readonly object _lock = new();
        private bool _running;
        private bool _dirty;

        public bool Mark()
        {
            lock (_lock)
            {
                if (_running) { _dirty = true; return false; }
                _running = true;
                return true;
            }
        }

        public bool ResetAndContinue()
        {
            lock (_lock)
            {
                if (_dirty) { _dirty = false; return true; }
                _running = false;
                return false;
            }
        }

        public void ForceStop()
        {
            lock (_lock) { _running = false; _dirty = false; }
        }
    }
}
