using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Продуктовая аналитика: эмиссия доменных событий (воронки/удержание). Switchable — в проде PostHog
/// (по API-ключу), иначе no-op (dev/тесты). Вызовы неблокирующие и «best-effort»: аналитика НЕ влияет
/// на пользовательский путь и не роняет запрос. Не передавать PII (особенно по детям) — только id/sub.
/// </summary>
public interface IAnalytics
{
    void Capture(string @event, string distinctId, IReadOnlyDictionary<string, object?>? properties = null);
}

/// <summary>Заглушка: аналитика отключена (нет ключа PostHog).</summary>
public sealed class NoopAnalytics : IAnalytics
{
    public void Capture(string @event, string distinctId, IReadOnlyDictionary<string, object?>? properties = null) { }
}

/// <summary>Отправляет события в PostHog (capture API). Fire-and-forget, ошибки только логируются.</summary>
internal sealed class PostHogAnalytics(HttpClient http, string apiKey, ILogger<PostHogAnalytics> log) : IAnalytics
{
    public void Capture(string @event, string distinctId, IReadOnlyDictionary<string, object?>? properties = null)
        => _ = SendAsync(@event, distinctId, properties);

    private async Task SendAsync(string @event, string distinctId, IReadOnlyDictionary<string, object?>? properties)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["api_key"] = apiKey,
                ["event"] = @event,
                ["distinct_id"] = distinctId,
                ["properties"] = properties is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>(properties),
            };
            using var resp = await http.PostAsJsonAsync("capture/", payload);
            if (!resp.IsSuccessStatusCode)
                log.LogDebug("PostHog отклонил событие {Event}: {Status}", @event, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "PostHog: событие {Event} не отправлено.", @event);
        }
    }
}

public static class AnalyticsExtensions
{
    /// <summary>
    /// Регистрирует <see cref="IAnalytics"/>. Есть ключ <c>Analytics:PostHog:ApiKey</c> → PostHog
    /// (host из <c>Analytics:PostHog:Host</c>, по умолчанию EU-облако — важно для приватности данных детей);
    /// нет ключа → no-op (dev/тесты не шлют наружу).
    /// </summary>
    public static IHostApplicationBuilder AddChessSchoolAnalytics(this IHostApplicationBuilder builder)
    {
        var apiKey = builder.Configuration["Analytics:PostHog:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            builder.Services.AddSingleton<IAnalytics, NoopAnalytics>();
            return builder;
        }

        var host = builder.Configuration["Analytics:PostHog:Host"] ?? "https://eu.i.posthog.com";
        builder.Services.AddHttpClient("posthog", c => c.BaseAddress = new Uri(host));
        builder.Services.AddSingleton<IAnalytics>(sp => new PostHogAnalytics(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("posthog"),
            apiKey,
            sp.GetRequiredService<ILogger<PostHogAnalytics>>()));
        return builder;
    }
}
