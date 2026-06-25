using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace ChessSchool.Auth;

/// <summary>
/// Регистрирует OAuth2/OIDC-клиентов (веб-приложения) в OpenIddict при старте.
/// Базовые адреса приходят из конфигурации (Aspire прокидывает фактические URL),
/// поэтому redirect_uri всегда актуальны при динамических портах.
/// </summary>
public sealed class ClientSeeder(IServiceProvider services, IConfiguration config) : IHostedService
{
    private static readonly string[] ClientIds = ["chessschool-web", "arena-web"];

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

        // Scope chess.api связан с ресурсом chessschool-api → aud токена выставляется автоматически.
        if (await scopeManager.FindByNameAsync("chess.api", ct) is null)
        {
            await scopeManager.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = "chess.api",
                DisplayName = "ChessSchool API",
                Resources = { "chessschool-api" }
            }, ct);
        }

        foreach (var clientId in ClientIds)
        {
            var baseUrl = config[$"Sso:Clients:{clientId}"];
            if (string.IsNullOrEmpty(baseUrl)) continue;

            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientType = ClientTypes.Public, // публичный клиент (PKCE, без секрета)
                DisplayName = clientId,
                RedirectUris = { new Uri($"{baseUrl}/signin-oidc") },
                PostLogoutRedirectUris = { new Uri($"{baseUrl}/signout-callback-oidc") },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.EndSession,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Email,
                    Permissions.Scopes.Profile,
                    Permissions.Prefixes.Scope + "chess.api"
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange }
            };

            var existing = await manager.FindByClientIdAsync(clientId, ct);
            if (existing is null)
                await manager.CreateAsync(descriptor, ct);
            else
                await manager.UpdateAsync(existing, descriptor, ct); // обновляем redirect_uri (порты могли смениться)
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
