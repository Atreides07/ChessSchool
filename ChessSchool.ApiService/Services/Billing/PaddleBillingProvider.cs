using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChessSchool.Contracts;

namespace ChessSchool.ApiService.Services.Billing;

/// <summary>Конфиг Paddle Billing (секреты — из user-secrets/KMS, price/токен — из конфига).</summary>
public sealed class PaddleOptions
{
    public string? ApiKey { get; set; }          // server-side API key (sandbox/prod)
    public string? WebhookSecret { get; set; }   // секрет подписи вебхуков (notification destination)
    public string? ClientToken { get; set; }     // client-side token для Paddle.js
    public string? PremiumPriceId { get; set; }   // price id плана премиум (pri_...)
    public string Environment { get; set; } = "sandbox"; // sandbox | production
}

/// <summary>
/// Paddle Billing: запуск hosted Checkout (Paddle.js v2) на клиенте — карты у нас не ходят. Разбор и
/// верификацию вебхуков делает <see cref="PaddleWebhook"/>. Выбирается, когда задан Paddle:WebhookSecret/ApiKey.
/// </summary>
public sealed class PaddleBillingProvider(PaddleOptions options, IHttpClientFactory httpFactory,
    ILogger<PaddleBillingProvider> logger) : IBillingProvider
{
    public const string HttpClientName = "paddle";

    public string Name => "paddle";

    public BillingCheckout CreateCheckout(string userSub, string plan) =>
        new(Name, DevAutoActivate: false, ClientToken: options.ClientToken, PriceId: options.PremiumPriceId,
            CustomData: userSub, Environment: options.Environment);

    /// <summary>Вытягивает текущее состояние подписки из Paddle (GET /subscriptions/{id}) — reconcile,
    /// если вебхук не пришёл/опоздал. null — не нашли/нет ключа/ошибка.</summary>
    public async Task<BillingEventDto?> FetchSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        var json = await GetAsync($"/subscriptions/{subscriptionId}", ct);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("data", out var data)
                && PaddleWebhook.TryMapSubscription(data, $"reconcile-{subscriptionId}", out var ev) ? ev : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>По transaction id (из success-URL checkout, `_ptxn`) находит подписку и вытягивает её
    /// состояние — активирует премиум, даже если вебхук активации не дошёл.</summary>
    public async Task<BillingEventDto?> FetchByTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        var json = await GetAsync($"/transactions/{transactionId}", ct);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            var subId = PaddleWebhook.Field(data, "subscription_id");
            return string.IsNullOrEmpty(subId) ? null : await FetchSubscriptionAsync(subId, ct);
        }
        catch (JsonException) { return null; }
    }

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) return null;
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            using var resp = await client.SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Paddle GET {Path} не удался.", path);
            return null;
        }
    }

    /// <summary>Создаёт сессию Customer Portal (Paddle API) и возвращает общий URL обзора подписки.</summary>
    public async Task<string?> CreatePortalUrlAsync(string providerCustomerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerCustomerId) || string.IsNullOrWhiteSpace(options.ApiKey)) return null;
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/customers/{providerCustomerId}/portal-sessions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
            req.Content = System.Net.Http.Json.JsonContent.Create(new { });
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return ExtractOverviewUrl(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось создать сессию Customer Portal для {Customer}.", providerCustomerId);
            return null;
        }
    }

    /// <summary>Достаёт data.urls.general.overview из ответа Paddle portal-sessions.</summary>
    public static string? ExtractOverviewUrl(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("urls", out var urls)
                && urls.TryGetProperty("general", out var general)
                && general.TryGetProperty("overview", out var overview)
                && overview.ValueKind == JsonValueKind.String
                ? overview.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Верификация и разбор вебхуков Paddle Billing. Подпись: заголовок Paddle-Signature вида
/// "ts=...;h1=..." — HMAC-SHA256 от строки "{ts}:{rawBody}" на секрете назначения, сравнение с h1
/// в постоянное время; проверяется и свежесть ts (анти-replay). Источник истины о статусе — наш стор.
/// </summary>
public static class PaddleWebhook
{
    public static bool VerifySignature(string body, string? signatureHeader, string secret,
        DateTimeOffset now, int toleranceSeconds = 300)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(secret)) return false;

        string? ts = null, h1 = null;
        foreach (var part in signatureHeader.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "ts") ts = kv[1];
            else if (kv[0] == "h1") h1 = kv[1];
        }
        if (ts is null || h1 is null || !long.TryParse(ts, out var tsUnix)) return false;

        // Анти-replay: подпись не должна быть слишком старой/из будущего.
        if (Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(tsUnix)).TotalSeconds) > toleranceSeconds) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}:{body}")));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(h1));
    }

    /// <summary>Разбирает событие subscription.* вебхука в нормализованное BillingEventDto.</summary>
    public static bool TryParse(string body, out BillingEventDto? ev)
    {
        ev = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var eventId = Str(root, "event_id");
            var type = Str(root, "event_type") ?? "";
            if (string.IsNullOrEmpty(eventId) || !type.StartsWith("subscription.")) return false;
            return root.TryGetProperty("data", out var data) && TryMapSubscription(data, eventId!, out ev);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Маппит объект subscription (из вебхука или ответа GET /subscriptions/{id}) в BillingEventDto.
    /// Общий код для вебхука и «вытягивания» статуса из API (reconcile). false — нет статуса/user_sub.
    /// </summary>
    public static bool TryMapSubscription(JsonElement data, string eventId, out BillingEventDto? ev)
    {
        ev = null;
        var mapped = MapStatus(Str(data, "status"));
        if (mapped is null) return false;

        // user_sub передаём в custom_data при checkout — связывает подписку с пользователем.
        string? userSub = null;
        if (data.TryGetProperty("custom_data", out var cd) && cd.ValueKind == JsonValueKind.Object)
            userSub = Str(cd, "user_sub");
        if (string.IsNullOrEmpty(userSub)) return false;

        string? priceId = null;
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            && items.GetArrayLength() > 0 && items[0].TryGetProperty("price", out var price))
            priceId = Str(price, "id");

        DateTimeOffset? periodEnd = null;
        if (data.TryGetProperty("current_billing_period", out var cbp) && cbp.ValueKind == JsonValueKind.Object
            && Str(cbp, "ends_at") is { } ends && DateTimeOffset.TryParse(ends, out var pe))
            periodEnd = pe;

        ev = new BillingEventDto(eventId, userSub!, mapped.Value, "premium",
            ProviderSubscriptionId: Str(data, "id"), ProviderCustomerId: Str(data, "customer_id"),
            PriceId: priceId, CurrentPeriodEnd: periodEnd);
        return true;
    }

    /// <summary>Достаёт строковое поле верхнего уровня (для разбора ответов API).</summary>
    public static string? Field(JsonElement e, string prop) => Str(e, prop);

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static SubscriptionStatus? MapStatus(string? s) => s switch
    {
        "active" => SubscriptionStatus.Active,
        "trialing" => SubscriptionStatus.Trialing,
        "past_due" => SubscriptionStatus.PastDue,
        "paused" => SubscriptionStatus.Paused,
        "canceled" => SubscriptionStatus.Canceled,
        _ => null,
    };
}
