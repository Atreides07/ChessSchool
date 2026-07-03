using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace ChessSchool.Auth;

/// <summary>
/// Распределённый fixed-window лимитер поверх Redis: счётчик общий для всех нод, поэтому суммарный лимит
/// не размножается на число реплик (в отличие от in-memory-лимитера — см. §Масштабирование). Окно
/// «от первого запроса»: атомарный <c>INCRBY</c> + <c>PEXPIRE</c> при первом инкременте (Lua — одна
/// round-trip, без гонок между нодами). При недоступности Redis — <b>fail-open</b> (пропускаем): лимитер
/// не должен превращать сбой Redis в отказ логина всем пользователям (доступность важнее в этом узле;
/// перебор в узкое окно аварии смягчается коротким TTL и восстановлением Redis).
/// </summary>
public sealed class RedisFixedWindowRateLimiter : RateLimiter
{
    // c = INCRBY key n; при первом инкременте вешаем TTL окна. Возвращаем текущий счётчик.
    private const string IncrScript =
        "local c = redis.call('INCRBY', KEYS[1], ARGV[1]) " +
        "if c == tonumber(ARGV[1]) then redis.call('PEXPIRE', KEYS[1], ARGV[2]) end " +
        "return c";

    private readonly IConnectionMultiplexer _mux;
    private readonly RedisKey _key;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly ILogger _log;
    private long _lastUsedTicks = DateTimeOffset.UtcNow.UtcTicks;

    public RedisFixedWindowRateLimiter(IConnectionMultiplexer mux, string key, int permitLimit, TimeSpan window, ILogger log)
    {
        _mux = mux;
        _key = key;
        _permitLimit = permitLimit;
        _window = window;
        _log = log;
    }

    // Даём фреймворку эвиктить простаивающие экземпляры лимитера (по одному на ключ/IP) — сам счётчик
    // в Redis живёт независимо (TTL окна), так что потеря локального экземпляра ничего не ломает.
    public override TimeSpan? IdleDuration =>
        TimeSpan.FromTicks(DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _lastUsedTicks));

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken ct)
    {
        Touch();
        if (permitCount == 0) return new Lease(true, null);
        try
        {
            var db = _mux.GetDatabase();
            var count = (long)await db.ScriptEvaluateAsync(IncrScript,
                [_key], [permitCount, (long)_window.TotalMilliseconds]);
            return count <= _permitLimit ? new Lease(true, null) : new Lease(false, _window);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Redis-лимитер недоступен — пропускаем запрос (fail-open).");
            return new Lease(true, null);
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        // Синхронный путь (middleware обычно идёт через AcquireAsync). Держим согласованное поведение.
        Touch();
        if (permitCount == 0) return new Lease(true, null);
        try
        {
            var db = _mux.GetDatabase();
            var count = (long)db.ScriptEvaluate(IncrScript,
                [_key], [permitCount, (long)_window.TotalMilliseconds]);
            return count <= _permitLimit ? new Lease(true, null) : new Lease(false, _window);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Redis-лимитер недоступен — пропускаем запрос (fail-open).");
            return new Lease(true, null);
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTimeOffset.UtcNow.UtcTicks);

    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;
        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter.Value;
                return true;
            }
            metadata = null;
            return false;
        }
    }
}
