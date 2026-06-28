using System.Net.Http.Json;
using ChessSchool.Contracts;

namespace ChessSchool.Arena.Services;

/// <summary>Отправляет завершённую арена-партию в ApiService для архива/разбора (как GameServer для онлайна).</summary>
public interface IArenaGameArchiveClient
{
    /// <summary>Архивирует партию. Не бросает исключений (fire-and-forget из грейна): ошибки логируются.</summary>
    Task ArchiveAsync(ArenaGameArchiveRequest request);
}

/// <summary>
/// HTTP-клиент к ApiService (внутренний ключ). Идемпотентность — на стороне ApiService (по ExternalGameId).
/// Таймауты/ретраи/circuit-breaker даёт стандартный resilience-handler из ServiceDefaults.
/// </summary>
public sealed class ArenaGameArchiveClient(
    IHttpClientFactory httpFactory, string internalKey, ILogger<ArenaGameArchiveClient> log)
    : IArenaGameArchiveClient
{
    public const string HttpClientName = "apiservice-arena-archive";

    public async Task ArchiveAsync(ArenaGameArchiveRequest request)
    {
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/arena-games/archive")
            {
                Content = JsonContent.Create(request)
            };
            msg.Headers.Add("X-Internal-Key", internalKey);
            using var resp = await client.SendAsync(msg);
            if (!resp.IsSuccessStatusCode)
                log.LogWarning("Архивация арена-партии {GameId} отклонена API: {Status}",
                    request.GameId, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            // Архивация не критична для хода турнира — не роняем грейн, теряем максимум разбор одной партии.
            log.LogWarning(ex, "Не удалось заархивировать арена-партию {GameId}.", request.GameId);
        }
    }
}
