using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Внутрипроцессный pub/sub для push-обновлений арены. Грейн (тот же процесс) публикует
/// изменения по tournamentId, Blazor-компоненты подписываются и перерисовываются — без опроса.
/// </summary>
public sealed class ArenaNotifier
{
    private readonly ConcurrentDictionary<string, ImmutableList<Action>> _subscribers = new();

    public IDisposable Subscribe(string tournamentId, Action callback)
    {
        _subscribers.AddOrUpdate(tournamentId,
            _ => ImmutableList.Create(callback),
            (_, list) => list.Add(callback));
        return new Subscription(this, tournamentId, callback);
    }

    public void Notify(string tournamentId)
    {
        if (_subscribers.TryGetValue(tournamentId, out var list))
            foreach (var cb in list) cb();
    }

    private void Unsubscribe(string tournamentId, Action callback)
    {
        _subscribers.AddOrUpdate(tournamentId,
            _ => ImmutableList<Action>.Empty,
            (_, list) => list.Remove(callback));
    }

    private sealed class Subscription(ArenaNotifier owner, string id, Action cb) : IDisposable
    {
        public void Dispose() => owner.Unsubscribe(id, cb);
    }
}
