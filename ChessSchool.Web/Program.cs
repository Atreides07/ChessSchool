using System.Text.Json;
using ChessSchool.Web.Clients;
using ChessSchool.Web.Components;
using ChessSchool.WebAuth;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// За обратным прокси (Aspire/ingress) доверяем forwarded-заголовкам, чтобы OIDC строил redirect_uri
// по внешнему хосту, а не по внутреннему порту Kestrel.
builder.AddChessSchoolForwardedHeaders();

// Единый вход (SSO) через общий IdP — тот же аккаунт, что и в Arena.
builder.AddChessSchoolSso();

// Readiness-проверка Redis (в /health, не в /alive).
if (builder.Configuration.GetRedisConnectionString() is { } webRedis)
    builder.Services.AddHealthChecks().AddRedis(webRedis, name: "redis");

// HTTP-клиент к IdP для обновления access-токена по refresh_token (эндпоинт /api/game-token).
builder.Services.AddHttpClient("idp", c => c.BaseAddress = new("https+http://auth"));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOutputCache();

// Доменный API и сервис авторизации — через service discovery Aspire.
builder.Services.AddHttpClient<SchoolApiClient>(c => c.BaseAddress = new("https+http://apiservice"));
builder.Services.AddHttpClient<AuthApiClient>(c => c.BaseAddress = new("https+http://auth"));

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseOutputCache();
app.MapStaticAssets();

app.MapSsoEndpoints();

// Свежий access-токен для браузерного SignalR-клиента (тонкий фронт).
// При истечении обновляется по refresh_token — клиент всегда получает валидный токен.
app.MapGet("/api/game-token", async (HttpContext ctx, IHttpClientFactory hf) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();

    var access = await ctx.GetTokenAsync("access_token");
    var expiresAt = await ctx.GetTokenAsync("expires_at");
    var refresh = await ctx.GetTokenAsync("refresh_token");

    var expired = !DateTimeOffset.TryParse(expiresAt, out var exp) || exp <= DateTimeOffset.UtcNow.AddSeconds(30);
    if (expired && !string.IsNullOrEmpty(refresh))
    {
        var resp = await hf.CreateClient("idp").PostAsync("/connect/token", new FormUrlEncodedContent(
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
            access = root.GetProperty("access_token").GetString();

            // Сохраняем обновлённые токены в cookie, чтобы переиспользовать до следующего истечения.
            var auth = await ctx.AuthenticateAsync();
            if (auth.Properties is not null && access is not null)
            {
                auth.Properties.UpdateTokenValue("access_token", access);
                if (root.TryGetProperty("refresh_token", out var rt)) auth.Properties.UpdateTokenValue("refresh_token", rt.GetString()!);
                if (root.TryGetProperty("expires_in", out var ei))
                    auth.Properties.UpdateTokenValue("expires_at", DateTimeOffset.UtcNow.AddSeconds(ei.GetInt32()).ToString("o"));
                await ctx.SignInAsync(auth.Principal!, auth.Properties);
            }
        }
    }

    return string.IsNullOrEmpty(access) ? Results.Unauthorized() : Results.Json(new { access_token = access });
}).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
