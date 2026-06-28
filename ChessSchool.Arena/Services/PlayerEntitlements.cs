using System.Net.Http.Json;
using ChessSchool.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace ChessSchool.Arena.Services;

/// <summary>Премиум-доступ игрока. Единственная точка гейтинга фич в Arena.</summary>
public interface IPlayerEntitlements
{
    Task<bool> IsPremiumAsync(string? sub, CancellationToken ct = default);

    /// <summary>Сбросить кэш для пользователя — после reconcile/активации, чтобы статус подхватился сразу
    /// (на этой ноде; на других истечёт по TTL). Источник истины — ApiService, кэш лишь ускоритель.</summary>
    void Invalidate(string? sub);
}

/// <summary>
/// Спрашивает статус подписки у ApiService (источник истины) по внутреннему ключу и кэширует на ноду
/// (локальный ускоритель над общим стором, короткий TTL — консистентность достаточная). Деградация
/// безопасна: ApiService недоступен → считаем БЕЗ премиума (платное не открываем бесплатно).
/// </summary>
public sealed class PlayerEntitlements(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    string internalKey,
    ILogger<PlayerEntitlements> logger) : IPlayerEntitlements
{
    public const string HttpClientName = "apiservice";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static string CacheKey(string sub) => $"premium:{sub}";

    public void Invalidate(string? sub)
    {
        if (!string.IsNullOrEmpty(sub)) cache.Remove(CacheKey(sub));
    }

    public async Task<bool> IsPremiumAsync(string? sub, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sub)) return false;
        var key = CacheKey(sub);
        if (cache.TryGetValue(key, out bool cached)) return cached;

        bool premium = false;
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/internal/subscriptions/{Uri.EscapeDataString(sub)}");
            req.Headers.Add("X-Internal-Key", internalKey);
            using var resp = await client.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var dto = await resp.Content.ReadFromJsonAsync<SubscriptionDto>(ct);
                premium = dto?.IsPremium ?? false;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось получить подписку {Sub} — считаем без премиума.", sub);
            premium = false;
        }

        cache.Set(key, premium, Ttl);
        return premium;
    }
}
