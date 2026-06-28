using System.Net;
using System.Net.Http.Json;
using ChessSchool.Contracts;
using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Services;

/// <summary>Клиент к ApiService (источник истины) для истории арена-партий, деталей и кэша разбора.
/// Сетевые сбои (ApiService недоступен/таймаут) не валят страницу — деградируем в пустой результат/null.</summary>
public sealed class ArenaGamesApiClient(IHttpClientFactory httpFactory, string internalKey,
    ILogger<ArenaGamesApiClient> log)
{
    // Переиспользуем тот же HttpClient, что и entitlement (база — apiservice).
    private HttpClient Client()
    {
        var c = httpFactory.CreateClient(PlayerEntitlements.HttpClientName);
        return c;
    }

    private HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Internal-Key", internalKey);
        return r;
    }

    public async Task<ArenaGameListPage> ListAsync(string sub, int skip, int take, CancellationToken ct)
    {
        try
        {
            using var req = Req(HttpMethod.Get, $"/internal/arena-games?sub={Uri.EscapeDataString(sub)}&skip={skip}&take={take}");
            using var resp = await Client().SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new ArenaGameListPage([], 0);
            return await resp.Content.ReadFromJsonAsync<ArenaGameListPage>(ct) ?? new ArenaGameListPage([], 0);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            log.LogWarning(ex, "ApiService недоступен при запросе истории партий — отдаю пустой список.");
            return new ArenaGameListPage([], 0);
        }
    }

    public async Task<ArenaGameDetail?> GetAsync(Guid id, string sub, CancellationToken ct)
    {
        try
        {
            using var req = Req(HttpMethod.Get, $"/internal/arena-games/{id}?sub={Uri.EscapeDataString(sub)}");
            using var resp = await Client().SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<ArenaGameDetail>(ct) : null;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            log.LogWarning(ex, "ApiService недоступен при запросе партии {Id}.", id);
            return null;
        }
    }

    public async Task<GameAnalysisDto?> GetCachedAnalysisAsync(Guid id, string sub, CancellationToken ct)
    {
        try
        {
            using var req = Req(HttpMethod.Get, $"/internal/arena-games/{id}/analysis?sub={Uri.EscapeDataString(sub)}");
            using var resp = await Client().SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<GameAnalysisDto>(ct);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            log.LogWarning(ex, "ApiService недоступен при запросе кэша разбора {Id}.", id);
            return null;
        }
    }

    // Сетевой сбой/таймаут ApiService — деградируем; отмену самим вызывающим (ct) пробрасываем.
    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException
            or InvalidOperationException;

    public async Task SaveAnalysisAsync(Guid id, GameAnalysisDto dto, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Post, $"/internal/arena-games/{id}/analysis");
        req.Content = JsonContent.Create(dto);
        using var resp = await Client().SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}
