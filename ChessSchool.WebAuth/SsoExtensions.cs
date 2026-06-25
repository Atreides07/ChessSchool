using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChessSchool.WebAuth;

/// <summary>
/// Единый вход (SSO) для веб-приложений на стандартном OpenID Connect (authorization code + PKCE)
/// против общего IdP (OpenIddict). Вход один раз — обе системы (ChessSchool и Arena) распознают
/// пользователя по общей сессии IdP. Access-токен кладётся в claim сессии для использования в SignalR.
/// </summary>
public static class SsoExtensions
{
    public const string AccessTokenClaim = "access_token";

    public static void AddChessSchoolSso(this WebApplicationBuilder builder)
    {
        var clientId = builder.Configuration["Sso:ClientId"] ?? "app";
        var authority = builder.Configuration["services:auth:https:0"]
            ?? builder.Configuration["services:auth:http:0"];

        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(o =>
        {
            o.Cookie.Name = $"app_{clientId}";
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
        })
        .AddOpenIdConnect(o =>
        {
            o.Authority = authority;
            o.ClientId = clientId;
            o.RequireHttpsMetadata = false;   // dev: self-signed
            o.ResponseType = "code";
            o.UsePkce = true;
            o.SaveTokens = true;
            o.GetClaimsFromUserInfoEndpoint = true;
            o.MapInboundClaims = false;
            o.TokenValidationParameters.NameClaimType = "name";

            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
            o.Scope.Add("chess.api");
            o.Scope.Add("offline_access"); // нужен refresh_token для обновления access-токена

            // Кладём токены в claims, чтобы Blazor-цепь (server-side) могла брать access-токен
            // для SignalR и обновлять его по refresh_token при истечении.
            o.Events = new OpenIdConnectEvents
            {
                OnTokenValidated = ctx =>
                {
                    var r = ctx.TokenEndpointResponse;
                    if (r?.AccessToken is { } at && ctx.Principal?.Identity is ClaimsIdentity identity)
                    {
                        identity.AddClaim(new Claim(AccessTokenClaim, at));
                        if (!string.IsNullOrEmpty(r.RefreshToken))
                            identity.AddClaim(new Claim("refresh_token", r.RefreshToken));
                        if (int.TryParse(r.ExpiresIn, out var exp))
                            identity.AddClaim(new Claim("token_expires_at", DateTimeOffset.UtcNow.AddSeconds(exp).ToString("o")));
                    }
                    return Task.CompletedTask;
                }
            };
        });

        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();

        // Серверное хранилище тикетов: в cookie остаётся только ключ, а большие OIDC-токены
        // (access/id/refresh) хранятся на сервере. Иначе cookie раздувается и Kestrel отвечает HTTP 431.
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ITicketStore, MemoryCacheTicketStore>();
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);
    }

    public static void MapSsoEndpoints(this WebApplication app)
    {
        // Вход: challenge OIDC. Если SSO-сессия IdP уже есть — вход проходит прозрачно, без формы.
        app.MapGet("/signin", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
                [OpenIdConnectDefaults.AuthenticationScheme]));

        app.MapGet("/signout", () =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
    }
}

/// <summary>
/// Хранит тикет аутентификации на сервере (in-memory); в cookie кладётся только короткий ключ.
/// Это держит cookie маленькой даже при больших OIDC-токенах. Для прод-многонодового сценария
/// заменить на распределённый кэш (Redis). При перезапуске сервиса тикеты теряются — нужен повторный вход.
/// </summary>
public sealed class MemoryCacheTicketStore(IMemoryCache cache) : ITicketStore
{
    private const string Prefix = "auth-ticket:";

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Prefix + Guid.NewGuid().ToString("N");
        return RenewAsync(key, ticket).ContinueWith(_ => key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var options = new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(8) };
        if (ticket.Properties.ExpiresUtc is { } exp) options.AbsoluteExpiration = exp.AddHours(8);
        cache.Set(key, ticket, options);
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) =>
        Task.FromResult(cache.Get<AuthenticationTicket>(key));

    public Task RemoveAsync(string key)
    {
        cache.Remove(key);
        return Task.CompletedTask;
    }
}
