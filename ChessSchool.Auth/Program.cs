using System.Security.Claims;
using ChessSchool.Auth;
using ChessSchool.Auth.Data;
using ChessSchool.Contracts;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Провайдер БД: sqlite (дефолт) | postgres. Хранилище OpenIddict — в том же контексте.
var dbProvider = builder.Configuration["Database:Provider"] ?? "sqlite";
builder.Services.AddDbContext<AuthDbContext>(o =>
{
    if (dbProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
    {
        o.UseNpgsql(builder.Configuration.GetConnectionString("auth"));
    }
    else
    {
        var sqlitePath = builder.Configuration.GetConnectionString("auth")
            ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "data", "auth.db")}";
        Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));
        o.UseSqlite(sqlitePath);
    }
    o.UseOpenIddict(); // регистрирует сущности OpenIddict в модели
});

builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddProblemDetails();

// Cookie-сессия IdP: единый вход (страница логина одна для всех приложений).
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "idp";
})
.AddCookie("idp", o =>
{
    o.Cookie.Name = "idp_sso";
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.LoginPath = "/account/login";
    o.ReturnUrlParameter = "return";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});
builder.Services.AddAuthorization();

// ---- OpenIddict: полноценный OIDC-сервер (authorization code + PKCE, refresh) ----
builder.Services.AddOpenIddict()
    .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
    .AddServer(o =>
    {
        o.SetAuthorizationEndpointUris("connect/authorize")
         .SetTokenEndpointUris("connect/token")
         .SetUserInfoEndpointUris("connect/userinfo")
         .SetEndSessionEndpointUris("connect/logout");

        o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
        o.RequireProofKeyForCodeExchange();
        o.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, "chess.api");

        // Dev-сертификаты. Access-токен НЕ шифруем — чтобы ресурс-серверы валидировали JWT по JWKS.
        o.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
        o.DisableAccessTokenEncryption();

        o.UseAspNetCore()
         .EnableAuthorizationEndpointPassthrough()
         .EnableTokenEndpointPassthrough()
         .EnableUserInfoEndpointPassthrough()
         .EnableEndSessionEndpointPassthrough();
    })
    .AddValidation(o =>
    {
        o.UseLocalServer();
        o.UseAspNetCore();
    });

builder.Services.AddHostedService<ClientSeeder>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    // SQLite (дефолт) — через миграции (схема версионируется, не ломается на апдейтах).
    // Postgres — EnsureCreated (отдельные prod-миграции вне этого скелета).
    if (dbProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase)) db.Database.EnsureCreated();
    else db.Database.Migrate();
}

app.UseAuthentication();
app.UseAuthorization();

// ---------------- Страница входа / регистрации (cookie-сессия IdP) ----------------
app.MapGet("/account/login", (string? @return, string? error) =>
    Results.Content(LoginPage(@return ?? "/", error), "text/html; charset=utf-8"));

app.MapPost("/account/login", async (HttpContext ctx, AuthDbContext db, IPasswordHasher<AppUser> hasher) =>
{
    var form = await ctx.Request.ReadFormAsync();
    string email = form["email"].ToString().Trim().ToLowerInvariant();
    string ret = form["return"].ToString();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, form["password"]!) == PasswordVerificationResult.Failed)
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1");

    await SignInCookieAsync(ctx, user);
    return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
});

app.MapPost("/account/register", async (HttpContext ctx, AuthDbContext db, IPasswordHasher<AppUser> hasher) =>
{
    var form = await ctx.Request.ReadFormAsync();
    string email = form["email"].ToString().Trim().ToLowerInvariant();
    string ret = form["return"].ToString();
    string password = form["password"]!;
    if (string.IsNullOrWhiteSpace(email) || password.Length < 6 || await db.Users.AnyAsync(u => u.Email == email))
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1");

    var user = new AppUser { Email = email, DisplayName = form["name"].ToString() };
    user.PasswordHash = hasher.HashPassword(user, password);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    await SignInCookieAsync(ctx, user);
    return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
});

// ---------------- OpenIddict: authorization endpoint ----------------
app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext ctx, AuthDbContext db,
    IOpenIddictScopeManager scopeManager) =>
{
    var request = ctx.GetOpenIddictServerRequest()
        ?? throw new InvalidOperationException("Некорректный OpenID Connect запрос.");

    var result = await ctx.AuthenticateAsync("idp");
    if (!result.Succeeded)
    {
        // Не залогинен → ведём на единую страницу входа, потом возвращаемся сюда.
        var returnUrl = ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = returnUrl },
            ["idp"]);
    }

    var sub = result.Principal!.FindFirst("sub")!.Value;
    var user = await db.Users.FindAsync(Guid.Parse(sub))
        ?? throw new InvalidOperationException("Пользователь не найден.");

    var identity = new ClaimsIdentity(
        authenticationType: TokenValidationParameters.DefaultAuthenticationType,
        nameType: Claims.Name, roleType: Claims.Role);

    identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.Name, user.DisplayName);

    var principal = new ClaimsPrincipal(identity);
    principal.SetScopes(request.GetScopes());

    // aud токена выводится из ресурсов, привязанных к запрошенным scope (а не задаётся вручную).
    var resources = new List<string>();
    await foreach (var resource in scopeManager.ListResourcesAsync(principal.GetScopes()))
        resources.Add(resource);
    principal.SetResources(resources);

    foreach (var claim in principal.Claims)
        claim.SetDestinations(GetDestinations(claim));

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
            claim.SetDestinations(GetDestinations(claim));
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
    return user is null
        ? Results.Unauthorized()
        : Results.Json(new Dictionary<string, object>
        {
            [Claims.Subject] = user.Id.ToString(),
            [Claims.Email] = user.Email,
            [Claims.Name] = user.DisplayName
        });
});

// ---------------- Завершение SSO-сессии ----------------
app.MapMethods("/connect/logout", ["GET", "POST"], async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("idp");
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
});

// ---------------- Внутренний резолв email → sub (привязка ученика в ApiService) ----------------
app.MapPost("/internal/users/by-email", async (ByEmailRequest req, HttpRequest http, AuthDbContext db,
    IConfiguration config, CancellationToken ct) =>
{
    var key = config["InternalApiKey"] ?? "dev-internal-key";
    if (http.Headers["X-Internal-Key"] != key) return Results.Unauthorized();

    var email = req.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
    return user is null
        ? Results.NotFound()
        : Results.Ok(new { sub = user.Id.ToString(), displayName = user.DisplayName });
});

app.MapDefaultEndpoints();
app.Run();

static async Task SignInCookieAsync(HttpContext ctx, AppUser user)
{
    var identity = new ClaimsIdentity("idp");
    identity.AddClaim(new Claim("sub", user.Id.ToString()));
    identity.AddClaim(new Claim("name", user.DisplayName));
    identity.AddClaim(new Claim("email", user.Email));
    await ctx.SignInAsync("idp", new ClaimsPrincipal(identity));
}

static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
{
    Claims.Name or Claims.Email or Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
    _ => [Destinations.AccessToken]
};

static string LoginPage(string ret, string? error) => $$"""
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Вход — ChessSchool ID</title>
<style>body{font-family:system-ui,sans-serif;background:#0f1419;color:#eee;display:flex;justify-content:center;align-items:center;height:100vh;margin:0}
.card{background:#1b2430;padding:2rem;border-radius:12px;width:320px;box-shadow:0 8px 32px rgba(0,0,0,.4)}
h1{font-size:1.3rem;margin:0 0 1rem}input{width:100%;padding:.6rem;margin:.3rem 0;border-radius:6px;border:1px solid #334;background:#0f1419;color:#eee;box-sizing:border-box}
button{width:100%;padding:.6rem;margin-top:.5rem;border:0;border-radius:6px;background:#1b6ec2;color:#fff;font-weight:600;cursor:pointer}
.err{color:#ff6b6b;font-size:.85rem}.muted{color:#8b98a5;font-size:.8rem;text-align:center;margin-top:1rem}</style></head>
<body><div class="card"><h1>♟ ChessSchool ID</h1>
{{(error is not null ? "<p class=\"err\">Неверные данные или email занят.</p>" : "")}}
<form method="post" action="/account/login">
<input type="hidden" name="return" value="{{System.Net.WebUtility.HtmlEncode(ret)}}">
<input name="email" type="email" placeholder="email" required>
<input name="password" type="password" placeholder="пароль" required>
<button type="submit">Войти</button></form>
<form method="post" action="/account/register">
<input type="hidden" name="return" value="{{System.Net.WebUtility.HtmlEncode(ret)}}">
<input name="name" placeholder="имя (для регистрации)">
<input name="email" type="email" placeholder="email">
<input name="password" type="password" placeholder="пароль (мин. 6)">
<button type="submit" style="background:#2d7d46">Зарегистрироваться</button></form>
<p class="muted">Единый аккаунт для ChessSchool и Arena</p></div></body></html>
""";

record ByEmailRequest(string Email);
