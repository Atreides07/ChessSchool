using System.Net;
using System.Net.Http.Json;
using ChessSchool.Contracts;

namespace ChessSchool.Arena.Services;

/// <summary>Клиент к ApiService (источник истины) для истории арена-партий, деталей и кэша разбора.</summary>
public sealed class ArenaGamesApiClient(IHttpClientFactory httpFactory, string internalKey)
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
        using var req = Req(HttpMethod.Get, $"/internal/arena-games?sub={Uri.EscapeDataString(sub)}&skip={skip}&take={take}");
        using var resp = await Client().SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new ArenaGameListPage([], 0);
        return await resp.Content.ReadFromJsonAsync<ArenaGameListPage>(ct) ?? new ArenaGameListPage([], 0);
    }

    public async Task<ArenaGameDetail?> GetAsync(Guid id, string sub, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Get, $"/internal/arena-games/{id}?sub={Uri.EscapeDataString(sub)}");
        using var resp = await Client().SendAsync(req, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<ArenaGameDetail>(ct) : null;
    }

    public async Task<GameAnalysisDto?> GetCachedAnalysisAsync(Guid id, string sub, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Get, $"/internal/arena-games/{id}/analysis?sub={Uri.EscapeDataString(sub)}");
        using var resp = await Client().SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<GameAnalysisDto>(ct);
    }

    public async Task SaveAnalysisAsync(Guid id, GameAnalysisDto dto, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Post, $"/internal/arena-games/{id}/analysis");
        req.Content = JsonContent.Create(dto);
        using var resp = await Client().SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}
