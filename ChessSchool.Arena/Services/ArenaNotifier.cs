using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Pub/sub для push-обновлений арены. Грейн публикует изменения по tournamentId, Blazor-компоненты
/// подписываются и перерисовываются — без опроса. Есть Redis → публикация идёт в общий канал, и КАЖДАЯ
/// нода арены доставляет её своим локальным подписчикам (зритель на ноде B видит обновление турнира,
/// чей грейн живёт на ноде A). Нет Redis → внутрипроцессно (dev, одна нода).
/// </summary>
public sealed class ArenaNotifier : IDisposable
{
    private const string Channel = "arena:notify";
    private readonly ConcurrentDictionary<string, ImmutableList<Action>> _subscribers = new();
    private readonly ILogger<ArenaNotifier> _log;
    private readonly ConnectionMultiplexer? _mux;
    private readonly ISubscriber? _bus;

    public ArenaNotifier(IConfiguration config, ILogger<ArenaNotifier> log)
    {
        _log = log;
        var conn = config.GetRedisConnectionString();
        if (conn is null) return;
        try
        {
            _mux = ConnectionMultiplexer.Connect(conn);
            _bus = _mux.GetSubscriber();
            // Любая нода (включая ту, где живёт грейн) получает публикацию и раздаёт локальным подписчикам.
            _bus.Subscribe(RedisChannel.Literal(Channel), (_, value) =>
            {
                if (value.HasValue) DispatchLocal(value!);
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Redis pub/sub арены недоступен — push-обновления только в пределах ноды.");
            _mux = null;
            _bus = null;
        }
    }

    public IDisposable Subscribe(string tournamentId, Action callback)
    {
        _subscribers.AddOrUpdate(tournamentId,
            _ => ImmutableList.Create(callback),
            (_, list) => list.Add(callback));
        return new Subscription(this, tournamentId, callback);
    }

    public void Notify(string tournamentId)
    {
        if (_bus is not null)
            _ = _bus.PublishAsync(RedisChannel.Literal(Channel), tournamentId);
        else
            DispatchLocal(tournamentId);
    }

    private void DispatchLocal(string tournamentId)
    {
        if (!_subscribers.TryGetValue(tournamentId, out var list)) return;
        foreach (var cb in list)
        {
            try { cb(); }
            catch (Exception ex) { _log.LogDebug(ex, "Подписчик арены бросил исключение."); }
        }
    }

    private void Unsubscribe(string tournamentId, Action callback)
    {
        _subscribers.AddOrUpdate(tournamentId,
            _ => ImmutableList<Action>.Empty,
            (_, list) => list.Remove(callback));
    }

    public void Dispose()
    {
        _bus?.UnsubscribeAll();
        _mux?.Dispose();
    }

    private sealed class Subscription(ArenaNotifier owner, string id, Action cb) : IDisposable
    {
        public void Dispose() => owner.Unsubscribe(id, cb);
    }
}
