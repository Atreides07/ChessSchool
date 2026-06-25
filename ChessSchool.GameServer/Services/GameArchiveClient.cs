using System.Net.Http.Json;
using ChessSchool.Contracts;

namespace ChessSchool.GameServer.Services;

/// <summary>Отправляет завершённую онлайн-партию в доменный API для архивации и пересчёта рейтинга.</summary>
public interface IGameArchiveClient
{
    Task ArchiveAsync(ArchiveGameRequest request);
}

public sealed class GameArchiveClient(HttpClient http, IConfiguration config) : IGameArchiveClient
{
    public async Task ArchiveAsync(ArchiveGameRequest request)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/games/archive")
        {
            Content = JsonContent.Create(request)
        };
        msg.Headers.Add("X-Internal-Key", config["InternalApiKey"] ?? "dev-internal-key");
        var response = await http.SendAsync(msg);
        response.EnsureSuccessStatusCode();
    }
}
