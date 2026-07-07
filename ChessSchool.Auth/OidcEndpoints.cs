using System.Security.Claims;
using ChessSchool.Auth.Data;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace ChessSchool.Auth;

/// <summary>
/// OpenID Connect эндпоинты OpenIddict: authorize (с гейтом обязательной MFA для админов), token
/// (code + refresh), userinfo и завершение SSO-сессии. Логика прежняя — вынесена из Program.cs в группу.
/// </summary>
public static class OidcEndpoints
{
    public static void MapOidcEndpoints(this WebApplication app, AuthConfig cfg)
    {
        // ---------------- OpenIddict: authorization endpoint ----------------
        app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext ctx, AuthDbContext db,
            IOpenIddictScopeManager scopeManager) =>
        {
            var request = ctx.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("Некорректный OpenID Connect запрос.");

            var returnUrl = ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
            var result = await ctx.AuthenticateAsync("idp");
            if (!result.Succeeded)
            {
                // Не залогинен → ведём на единую страницу входа, потом возвращаемся сюда.
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = returnUrl },
                    ["idp"]);
            }

            var sub = result.Principal!.FindFirst("sub")?.Value;
            var user = Guid.TryParse(sub, out var userId) ? await db.Users.FindAsync(userId) : null;
            if (user is null)
            {
                // Cookie-сессия ссылается на несуществующего пользователя (напр. cookie от прежней БД).
                // Не падаем 500 — гасим протухшую cookie и отправляем на повторный вход.
                await ctx.SignOutAsync("idp");
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = returnUrl },
                    ["idp"]);
            }

            // Обязательная MFA для админов — жёсткий гейт на выдаче токена: без включённой 2FA код не выдаём,
            // уводим админа в настройку (сессия IdP уже есть). Так админ не получит role=admin в приложении,
            // пока не включит второй фактор. Возврат — на исходный authorize-URL (после enrollment завершится вход).
            if (cfg.RequireMfaForAdmins && !user.MfaEnabled && AdminRoles.IsAdmin(cfg.AdminEmails, user.Email))
                return Results.Redirect($"/account/mfa?required=1&return={Uri.EscapeDataString(returnUrl)}");

            var identity = new ClaimsIdentity(
                authenticationType: TokenValidationParameters.DefaultAuthenticationType,
                nameType: Claims.Name, roleType: Claims.Role);

            identity.SetClaim(Claims.Subject, user.Id.ToString())
                    .SetClaim(Claims.Email, user.Email)
                    .SetClaim(Claims.EmailVerified, user.EmailConfirmed ? "true" : "false") // мягкий гейт: приложения гейтят по нему
                    .SetClaim(Claims.Name, user.DisplayName);

            // Ролевая модель: админам выдаём claim role=admin (едет в токен — см. GetDestinations).
            if (AdminRoles.IsAdmin(cfg.AdminEmails, user.Email))
                identity.SetClaim(Claims.Role, AdminRoles.Role);

            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(request.GetScopes());

            // aud токена выводится из ресурсов, привязанных к запрошенным scope (а не задаётся вручную).
            var resources = new List<string>();
            await foreach (var resource in scopeManager.ListResourcesAsync(principal.GetScopes()))
                resources.Add(resource);
            principal.SetResources(resources);

            foreach (var claim in principal.Claims)
                claim.SetDestinations(AccountFlow.GetDestinations(claim));

            return Results.SignIn(principal, new AuthenticationProperties(), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        });

        // ---------------- OpenIddict: token endpoint (code + refresh) ----------------
        app.MapPost("/connect/token", async (HttpContext ctx) =>
        {
            var request = ctx.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("Некорректный запрос токена.");

            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                var principal = (await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal!;
                foreach (var claim in principal.Claims)
                    claim.SetDestinations(AccountFlow.GetDestinations(claim));
                return Results.SignIn(principal, new AuthenticationProperties(), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            return Results.Problem("Неподдерживаемый тип гранта.", statusCode: 400);
        });

        // ---------------- OpenIddict: userinfo ----------------
        app.MapMethods("/connect/userinfo", ["GET", "POST"], async (HttpContext ctx, AuthDbContext db) =>
        {
            var principal = (await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
            if (principal is null) return Results.Unauthorized();

            var sub = principal.GetClaim(Claims.Subject);
            var user = sub is not null ? await db.Users.FindAsync(Guid.Parse(sub)) : null;
            if (user is null) return Results.Unauthorized();

            var claims = new Dictionary<string, object>
            {
                [Claims.Subject] = user.Id.ToString(),
                [Claims.Email] = user.Email,
                [Claims.EmailVerified] = user.EmailConfirmed,
                [Claims.Name] = user.DisplayName
            };
            // Роль — и в userinfo (потребитель мапит её в principal через GetClaimsFromUserInfoEndpoint).
            if (AdminRoles.IsAdmin(cfg.AdminEmails, user.Email)) claims[Claims.Role] = AdminRoles.Role;
            return Results.Json(claims);
        });

        // ---------------- Завершение SSO-сессии ----------------
        app.MapMethods("/connect/logout", ["GET", "POST"], async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync("idp");
            return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        });
    }
}
