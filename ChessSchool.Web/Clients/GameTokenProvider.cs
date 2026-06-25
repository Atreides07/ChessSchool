using System.Text.Json;
using ChessSchool.WebAuth;
using Microsoft.AspNetCore.Components.Authorization;

namespace ChessSchool.Web.Clients;

/// <summary>
/// Выдаёт актуальный access-токен для SignalR из Blazor-цепи (server-side).
/// Читает токены из claims сессии и обновляет по refresh_token при истечении,
/// кэшируя результат на время жизни цепи. Так /play не падает с 401 на протухшем токене.
/// </summary>
public sealed class GameTokenProvider(AuthenticationStateProvider auth, IHttpClientFactory http)
{
    private string? _cached;
    private DateTimeOffset _expiresAt;

    public async Task<string?> GetTokenAsync()
    {
        if (_cached is not null && _expiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
            return _cached;

        var user = (await auth.GetAuthenticationStateAsync()).User;
        var access = user.FindFirst(SsoExtensions.AccessTokenClaim)?.Value;
        var refresh = user.FindFirst("refresh_token")?.Value;
        DateTimeOffset.TryParse(user.FindFirst("token_expires_at")?.Value, out var exp);

        if (!string.IsNullOrEmpty(access) && exp > DateTimeOffset.UtcNow.AddSeconds(30))
            return Cache(access, exp);

        if (!string.IsNullOrEmpty(refresh))
        {
            var resp = await http.CreateClient("idp").PostAsync("/connect/token", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refresh,
                    ["client_id"] = "chessschool-web"
                }));
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var newAccess = root.GetProperty("access_token").GetString();
                var newExp = root.TryGetProperty("expires_in", out var ei)
                    ? DateTimeOffset.UtcNow.AddSeconds(ei.GetInt32())
                    : DateTimeOffset.UtcNow.AddMinutes(10);
                if (newAccess is not null) return Cache(newAccess, newExp);
            }
        }

        return access; // запасной вариант (возможно, протухший) — лучше, чем null
    }

    private string Cache(string token, DateTimeOffset exp)
    {
        _cached = token;
        _expiresAt = exp;
        return token;
    }
}
