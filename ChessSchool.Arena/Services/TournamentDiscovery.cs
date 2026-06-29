using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Services;

/// <summary>Не удалось получить список популярных турниров из внешнего источника (показывается админу).</summary>
public sealed class TournamentDiscoveryException(string message) : Exception(message);

/// <summary>
/// Кандидат на трансляцию из внешнего источника (lichess broadcasts) — предлагается админу к добавлению.
/// Чистые данные, без Orleans-сериализации (не персистится): живёт только в кэше ноды и ответе API.
/// </summary>
public sealed record TournamentSuggestion(
    string Slug,
    string Name,
    DateOnly Start,
    DateOnly End,
    string Location,
    string Format,
    string Url,
    string? ImageUrl,
    bool Live,
    /// <summary>Ссылка на «живой» PGN-фид (PGN раунда lichess) — прикрепляется к трансляции автоматически.</summary>
    string? PgnUrl = null);

/// <summary>
/// Поиск популярных шахматных турниров для админки трансляций: тянет курируемый топ официальных
/// трансляций lichess (<c>/api/broadcast/top</c>) и предлагает их админу к добавлению одним кликом.
///
/// Источник истины — внешний API; на ноде держим короткоживущий кэш (TTL), чтобы не дёргать lichess на
/// каждый заход в админку и не зависеть от его задержек. Кэш — лишь локальный ускоритель: переживает
/// потерю ноды (восстановим запросом) и при сбое источника отдаёт устаревший снимок (деградация, не
/// падение). Сетевой вызов выполняется ТОЛЬКО из request-контекста minimal-API, а не из лайфсайкла
/// Blazor-компонента (грабля #12 — там исходящий HTTP зависает). Регистрируется синглтоном (один кэш на ноду).
/// </summary>
public sealed class TournamentDiscovery(IHttpClientFactory httpFactory, ILogger<TournamentDiscovery> log)
{
    public const string HttpClientName = "tournament-discovery";
    private const string TopPath = "/api/broadcast/top";
    private const int MaxSuggestions = 24;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<TournamentSuggestion> _cache = [];
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Популярные турниры (активные + предстоящие), кэш с TTL. При сбое источника отдаёт устаревший
    /// снимок, если он есть; иначе бросает <see cref="TournamentDiscoveryException"/>.
    /// </summary>
    public async Task<IReadOnlyList<TournamentSuggestion>> PopularAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow < _expiresAt) return _cache;
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _expiresAt) return _cache; // другой поток уже обновил
            try
            {
                var client = httpFactory.CreateClient(HttpClientName);
                var json = await client.GetStringAsync(TopPath, ct);
                var baseUrl = client.BaseAddress?.GetLeftPart(UriPartial.Authority) ?? "https://lichess.org";
                _cache = Parse(json, DateOnly.FromDateTime(DateTime.UtcNow), MaxSuggestions, baseUrl);
                _expiresAt = DateTimeOffset.UtcNow + Ttl;
                return _cache;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Не удалось получить популярные турниры из {Source}.", TopPath);
                if (_cache.Count > 0) return _cache; // деградация: отдаём устаревший снимок
                throw new TournamentDiscoveryException("Источник популярных турниров недоступен.");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Найти кандидата по slug (для добавления). Использует тот же кэш с TTL.</summary>
    public async Task<TournamentSuggestion?> BySlugAsync(string slug, CancellationToken ct = default) =>
        (await PopularAsync(ct)).FirstOrDefault(s => s.Slug == slug);

    /// <summary>
    /// Разбор ответа lichess <c>/api/broadcast/top</c> в кандидатов. Берём секции <c>active</c> и
    /// <c>upcoming</c> (прошедшие не нужны), мапим только нужные поля. Чистая функция — тестируется без сети.
    /// </summary>
    public static IReadOnlyList<TournamentSuggestion> Parse(
        string json, DateOnly fallbackDate, int max = MaxSuggestions, string lichessBase = "https://lichess.org")
    {
        var list = new List<TournamentSuggestion>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return list;

        foreach (var (section, live) in new[] { ("active", true), ("upcoming", false) })
        {
            if (list.Count >= max) break;
            if (!root.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (list.Count >= max) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("tour", out var tour) || tour.ValueKind != JsonValueKind.Object) continue;
                if (MapTour(tour, item, live, fallbackDate, lichessBase) is { } s) list.Add(s);
            }
        }
        return list;
    }

    private static TournamentSuggestion? MapTour(JsonElement tour, JsonElement item, bool live, DateOnly fallback, string lichessBase)
    {
        var name = Str(tour, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var slug = Str(tour, "slug");
        if (string.IsNullOrWhiteSpace(slug)) slug = BroadcastFormat.Slugify(name);

        DateOnly start = fallback, end = fallback;
        if (tour.TryGetProperty("dates", out var dates) && dates.ValueKind == JsonValueKind.Array)
        {
            var ms = dates.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number)
                .Select(e => e.GetInt64()).ToArray();
            if (ms.Length > 0) start = FromMs(ms[0]);
            end = ms.Length > 1 ? FromMs(ms[1]) : start;
        }
        if (end < start) end = start;

        string location = "", format = "", website = "";
        if (tour.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            location = Str(info, "location");
            format = Str(info, "format");
            if (string.IsNullOrWhiteSpace(format)) format = Str(info, "tc");
            website = Str(info, "website");
        }

        var url = !string.IsNullOrWhiteSpace(website) ? website : Str(tour, "url");
        var image = Str(tour, "image");

        // Live-PGN раунда: lichess отдаёт PGN всех досок раунда по /api/broadcast/round/{id}.pgn —
        // прикрепляем автоматически, чтобы трансляция сразу показывала онлайн-доски.
        string? pgnUrl = null;
        if (item.TryGetProperty("round", out var round) && round.ValueKind == JsonValueKind.Object)
        {
            var roundId = Str(round, "id");
            if (!string.IsNullOrWhiteSpace(roundId))
                pgnUrl = $"{lichessBase.TrimEnd('/')}/api/broadcast/round/{roundId.Trim()}.pgn";
        }

        return new TournamentSuggestion(
            slug.Trim(), name.Trim(), start, end, location.Trim(), format.Trim(), url.Trim(),
            string.IsNullOrWhiteSpace(image) ? null : image.Trim(), live, pgnUrl);
    }

    /// <summary>«Wijk aan Zee, Netherlands» → ("Wijk aan Zee", "Netherlands"). Без запятой — всё в город.</summary>
    public static (string City, string Country) SplitLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return ("", "");
        var idx = location.LastIndexOf(',');
        return idx < 0 ? (location.Trim(), "") : (location[..idx].Trim(), location[(idx + 1)..].Trim());
    }

    /// <summary>
    /// Маппинг кандидата в запись каталога. <see cref="Broadcast.Visible"/> = false: добавляется скрытой,
    /// чтобы админ доклассифицировал (серия, флаг) и опубликовал. Изображение здесь не переносится —
    /// это делает вызывающий через <see cref="IImageIngestor"/> (асинхронно, в request-контексте).
    /// </summary>
    public static Broadcast ToBroadcast(TournamentSuggestion s)
    {
        var (city, country) = SplitLocation(s.Location);
        return new Broadcast
        {
            Slug = BroadcastFormat.IsValidSlug(s.Slug) ? s.Slug : BroadcastFormat.Slugify(s.Name),
            Name = s.Name,
            Series = "",
            SeriesCls = "cls",
            Start = s.Start,
            End = s.End,
            City = city,
            Country = country,
            Flag = "",
            Format = s.Format,
            Url = s.Url,
            ImageUrl = s.ImageUrl,
            PgnUrl = s.PgnUrl,
            Visible = false,
        };
    }

    private static DateOnly FromMs(long ms) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime);

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
