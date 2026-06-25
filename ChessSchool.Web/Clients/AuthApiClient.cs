using System.Net.Http.Json;
using ChessSchool.Contracts;

namespace ChessSchool.Web.Clients;

/// <summary>Клиент сервиса авторизации (IdP) для логина/регистрации игроков.</summary>
public sealed class AuthApiClient(HttpClient http)
{
    public Task<TokenResponse?> RegisterAsync(RegisterRequest req, CancellationToken ct = default) =>
        PostAsync("/register", req, ct);

    public Task<TokenResponse?> LoginAsync(LoginRequest req, CancellationToken ct = default) =>
        PostAsync("/login", req, ct);

    private async Task<TokenResponse?> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        var resp = await http.PostAsJsonAsync(path, body, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<TokenResponse>(ct) : null;
    }
}
