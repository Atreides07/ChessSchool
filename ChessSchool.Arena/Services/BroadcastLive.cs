using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Services;

/// <summary>Источник live-PGN трансляции недоступен (показывается зрителю — «трансляция временно недоступна»).</summary>
public sealed class BroadcastLiveException(string message) : Exception(message);

/// <summary>
/// Онлайн-доски трансляции: тянет «живой» мульти-партийный PGN из источника (<see cref="Broadcast.PgnUrl"/>)
/// и разбирает в доски с позициями (<see cref="BroadcastPgn"/>).
///
/// Источник истины — внешний фид; на ноде держим короткоживущий снимок на трансляцию (TTL), чтобы толпа
/// зрителей не дёргала источник на каждый запрос (источник опрашивается не чаще раза в TTL на ноду). Кэш —
/// локальный ускоритель: переживает потерю ноды (восстановим запросом) и при сбое источника отдаёт
/// предыдущий снимок (деградация, не падение). Сетевой запрос идёт из request-контекста minimal-API, а не
/// из лайфсайкла Blazor (грабля #12). Синглтон (снимки по slug на ноду).
///
/// Масштаб: при росте числа трансляций/нод централизованный опрос разумно вынести в грейн-на-трансляцию
/// (единственный опрашивающий + push через backplane) — точка расширения; сейчас пер-нодовый кэш достаточен.
/// </summary>
public sealed class BroadcastLive
{
    public const string HttpClientName = "broadcast-pgn";
    private const long MaxPgnBytes = 8 * 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly BroadcastsCatalog _catalog;
    private readonly ILogger<BroadcastLive> _log;
    private readonly TimeSpan _ttl;

    public BroadcastLive(IHttpClientFactory httpFactory, BroadcastsCatalog catalog,
        IConfiguration config, ILogger<BroadcastLive> log)
    {
        _httpFactory = httpFactory;
        _catalog = catalog;
        _log = log;
        var seconds = Math.Clamp(config.GetValue("Broadcasts:LivePollSeconds", 12), 3, 120);
        _ttl = TimeSpan.FromSeconds(seconds);
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IReadOnlyList<BroadcastBoard> Boards = [];
        public DateTimeOffset ExpiresAt = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    /// <summary>
    /// Доски трансляции по slug. null — трансляции нет, она скрыта или у неё не задан live-PGN. Бросает
    /// <see cref="BroadcastLiveException"/>, если источник недоступен и нет предыдущего снимка.
    /// </summary>
    public async Task<IReadOnlyList<BroadcastBoard>?> GetAsync(string slug, CancellationToken ct = default)
    {
        var broadcast = await _catalog.BySlugAsync(slug);
        if (broadcast is null || !broadcast.Visible || string.IsNullOrWhiteSpace(broadcast.PgnUrl)) return null;

        var entry = _cache.GetOrAdd(slug, _ => new Entry());
        if (DateTimeOffset.UtcNow < entry.ExpiresAt) return entry.Boards;

        await entry.Gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < entry.ExpiresAt) return entry.Boards; // другой поток уже обновил
            try
            {
                var pgn = await FetchAsync(broadcast.PgnUrl!, ct);
                entry.Boards = BroadcastPgn.Parse(pgn);
                entry.ExpiresAt = DateTimeOffset.UtcNow + _ttl;
                return entry.Boards;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Не удалось получить live-PGN трансляции {Slug} ({Url}).", slug, broadcast.PgnUrl);
                if (entry.Boards.Count > 0) return entry.Boards; // деградация: предыдущий снимок
                throw new BroadcastLiveException("Источник трансляции недоступен.");
            }
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new BroadcastLiveException("Некорректный адрес PGN-источника.");

        // SSRF-защита: адрес источника не должен указывать во внутреннюю сеть (даже если задан админом).
        if (!await IsHostPublicAsync(uri, ct))
            throw new BroadcastLiveException("Адрес источника ведёт во внутреннюю сеть.");

        var client = _httpFactory.CreateClient(HttpClientName);
        using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is > MaxPgnBytes)
            throw new BroadcastLiveException("PGN-фид больше допустимого размера.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await ReadCappedAsync(stream, MaxPgnBytes, ct);
    }

    private static async Task<string> ReadCappedAsync(Stream src, long cap, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap) throw new BroadcastLiveException("PGN-фид больше допустимого размера.");
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Хост резолвится и все его адреса публичны (нет loopback/частных/link-local — метаданные облака).</summary>
    private static async ValueTask<bool> IsHostPublicAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addrs;
        try { addrs = await Dns.GetHostAddressesAsync(uri.Host, ct); }
        catch (SocketException) { return false; }
        return addrs.Length > 0 && Array.TrueForAll(addrs, ip => !IsPrivate(ip));
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0
                || b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] >= 224;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
            || ip.Equals(IPAddress.IPv6Any)
            || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
}
