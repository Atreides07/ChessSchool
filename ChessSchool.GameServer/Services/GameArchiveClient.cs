using System.Net.Http.Json;
using ChessSchool.Contracts;

namespace ChessSchool.GameServer.Services;

/// <summary>Отправляет завершённую онлайн-партию в доменный API для архивации и пересчёта рейтинга.</summary>
public interface IGameArchiveClient
{
    Task ArchiveAsync(ArchiveGameRequest request, CancellationToken ct = default);
}

public sealed class GameArchiveClient : IGameArchiveClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GameArchiveClient> _log;
    private readonly string _internalKey;

    // Таймауты/ретраи/circuit-breaker даёт стандартный resilience-handler из ServiceDefaults.
    public GameArchiveClient(HttpClient http, IConfiguration config, IHostEnvironment env,
        ILogger<GameArchiveClient> log)
    {
        _http = http;
        _log = log;
        _internalKey = config.ResolveInternalApiKey(env);
    }

    public async Task ArchiveAsync(ArchiveGameRequest request, CancellationToken ct = default)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/games/archive")
        {
            Content = JsonContent.Create(request)
        };
        msg.Headers.Add("X-Internal-Key", _internalKey);

        var response = await _http.SendAsync(msg, ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("Архивация партии {GameId} отклонена API: {Status}",
                request.GameId, (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }
    }
}
