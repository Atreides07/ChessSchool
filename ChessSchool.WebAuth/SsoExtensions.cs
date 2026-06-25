using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

            // Кладём access-токен в claim, чтобы компоненты (SignalR) могли его взять из сессии.
            o.Events = new OpenIdConnectEvents
            {
                OnTokenValidated = ctx =>
                {
                    var accessToken = ctx.TokenEndpointResponse?.AccessToken;
                    if (accessToken is not null && ctx.Principal?.Identity is ClaimsIdentity identity)
                        identity.AddClaim(new Claim(AccessTokenClaim, accessToken));
                    return Task.CompletedTask;
                }
            };
        });

        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
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
