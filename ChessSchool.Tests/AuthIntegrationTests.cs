using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChessSchool.Auth.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace ChessSchool.Tests;

/// <summary>
/// Интеграционные тесты IdP против реального PostgreSQL (Testcontainers) — боевой провайдер БД.
/// Покрывают: (1) JWKS не утекает приватный материал ключа; (2) authorize не падает 500,
/// если cookie-сессия ссылается на отсутствующего пользователя (напр. cookie от прежней БД).
/// Требует Docker; образ postgres:18.3 переиспользуется (тот же, что у AppHost).
/// </summary>
public class AuthIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:18.3")
        .Build();

    private AuthFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _factory = new AuthFactory(_pg.GetConnectionString());
        // Принудительно стартуем хост (накат миграций + сидинг клиента) до тестов.
        _ = _factory.Services;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Jwks_ExposesOnlyPublicKeyMaterial()
    {
        var client = _factory.CreateClient();

        var json = await client.GetStringAsync("/.well-known/jwks");
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("keys");

        Assert.True(keys.GetArrayLength() >= 1, "JWKS должен содержать хотя бы один ключ подписи");
        foreach (var jwk in keys.EnumerateArray())
        {
            if (jwk.GetProperty("kty").GetString() == "RSA")
            {
                Assert.True(jwk.TryGetProperty("n", out _));
                Assert.True(jwk.TryGetProperty("e", out _));
            }
            // Приватные параметры RSA не должны попадать в публичный JWKS.
            foreach (var priv in new[] { "d", "p", "q", "dp", "dq", "qi" })
                Assert.False(jwk.TryGetProperty(priv, out _), $"JWKS не должен содержать приватный параметр '{priv}'");
        }
    }

    [Fact]
    public async Task Authorize_WhenCookieUserMissing_RedirectsToLogin_NotServerError()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 1) Регистрируемся — устанавливается cookie idp-сессии.
        var email = $"stale-{Guid.NewGuid():N}@test.local";
        var register = await client.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Stale User",
            ["email"] = email,
            ["password"] = "secret123",
            ["return"] = "/"
        }));
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);

        // 2) Удаляем пользователя из БД — теперь cookie указывает на несуществующего.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }

        // 3) Идём на authorize с (протухшей) cookie. Валидный OIDC-запрос + PKCE.
        var verifier = "test-verifier-0123456789-0123456789-0123456789";
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = Uri.EscapeDataString("https://localhost:5001/signin-oidc");
        var authorizeUrl =
            $"/connect/authorize?client_id=chessschool-web&redirect_uri={redirectUri}" +
            $"&response_type=code&scope=openid&code_challenge={challenge}&code_challenge_method=S256";

        var response = await client.GetAsync(authorizeUrl);

        // 4) Должны мягко уйти на повторный вход, а не 500.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location!.OriginalString);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class AuthFactory(string connectionString) : WebApplicationFactory<ChessSchool.Auth.AuthMarker>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development"); // OpenIddict снимает требование HTTPS-транспорта
            builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:authdb"] = connectionString,
                ["Sso:Clients:chessschool-web"] = "https://localhost:5001"
            }));
            return base.CreateHost(builder);
        }
    }
}
