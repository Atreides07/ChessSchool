using System.Net.Http.Json;
using ChessSchool.Contracts;

namespace ChessSchool.ApiService.Services;

/// <summary>Резолвленный из IdP пользователь (минимум для привязки/обогащения).</summary>
public sealed record ResolvedUser(string Sub, string DisplayName);

/// <summary>
/// Клиент к IdP (сервис авторизации) для резолва пользователей по e-mail и батчем по sub. Инкапсулирует
/// HTTP-плумбинг и внутренний ключ, чтобы эндпоинты не повторяли его (раньше дублировалось в 3 местах).
/// </summary>
public sealed class IdpUserClient(IHttpClientFactory httpFactory, string internalKey, ILogger<IdpUserClient> log)
{
    public const string HttpClientName = "auth";

    /// <summary>Найти пользователя по e-mail. null — не найден/IdP недоступен.</summary>
    public async Task<ResolvedUser?> ResolveByEmailAsync(string email, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/users/by-email")
        { Content = JsonContent.Create(new { email }) };
        msg.Headers.Add("X-Internal-Key", internalKey);
        using var resp = await client.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ResolvedUser>(ct);
    }

    /// <summary>Батч-резолв sub → профиль (для человекочитаемых списков). Деградирует тихо: сбой → пустая карта.</summary>
    public async Task<IReadOnlyDictionary<string, UserInfo>> ResolveBySubsAsync(IReadOnlyCollection<string> subs, CancellationToken ct)
    {
        var map = new Dictionary<string, UserInfo>(StringComparer.Ordinal);
        if (subs.Count == 0) return map;
        try
        {
            var client = httpFactory.CreateClient(HttpClientName);
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/users/by-subs")
            { Content = JsonContent.Create(new BySubsRequest(subs.Distinct().ToList())) };
            msg.Headers.Add("X-Internal-Key", internalKey);
            using var resp = await client.SendAsync(msg, ct);
            if (resp.IsSuccessStatusCode && await resp.Content.ReadFromJsonAsync<List<UserInfo>>(ct) is { } users)
                foreach (var u in users) map[u.Sub] = u;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось резолвить пользователей по sub в IdP — список без имён.");
        }
        return map;
    }
}
