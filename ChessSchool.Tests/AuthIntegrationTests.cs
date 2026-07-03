using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChessSchool.Auth.Data;
using ChessSchool.Auth.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace ChessSchool.Tests;

/// <summary>
/// Интеграционные тесты IdP против реального PostgreSQL (Testcontainers) — боевой провайдер БД.
/// Покрывают: JWKS без приватного материала; подтверждение e-mail при регистрации (создание
/// неподтверждённого + письмо со ссылкой, гейт логина, погашение токена → вход); мягкий редирект
/// при протухшей cookie. Письма перехватывает фейковый <see cref="IEmailSender"/>. Требует Docker.
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
        _ = _factory.Services; // старт хоста (миграции + сидинг) до тестов
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
            foreach (var priv in new[] { "d", "p", "q", "dp", "dq", "qi" })
                Assert.False(jwk.TryGetProperty(priv, out _), $"JWKS не должен содержать приватный параметр '{priv}'");
        }
    }

    [Fact]
    public async Task Register_CreatesUnconfirmedUser_AndSendsConfirmationEmail()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"reg-{Guid.NewGuid():N}@test.local";

        var register = await Register(client, email);

        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        Assert.Contains("mode=sent", register.Headers.Location!.OriginalString);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email);
            Assert.False(user.EmailConfirmed); // не подтверждён до перехода по ссылке
        }

        var msg = Assert.Single(_factory.Sent, m => m.To == email); // e-mail уникален на тест
        Assert.Contains("token=", msg.Html); // письмо содержит ссылку подтверждения
    }

    [Fact]
    public async Task Login_IsBlocked_UntilEmailConfirmed()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"gate-{Guid.NewGuid():N}@test.local";

        await Register(client, email);

        // 1) До подтверждения вход запрещён.
        var loginBefore = await Login(client, email);
        Assert.Equal(HttpStatusCode.Redirect, loginBefore.StatusCode);
        Assert.Contains("error=unconfirmed", loginBefore.Headers.Location!.OriginalString);

        // 2) Переходим по ссылке из письма → подтверждено.
        var confirm = await client.GetAsync($"/account/confirm?token={ConfirmToken(email)}");
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True((await db.Users.FirstAsync(u => u.Email == email)).EmailConfirmed);
        }

        // 3) Теперь вход проходит (без ошибки).
        var loginAfter = await Login(client, email);
        Assert.Equal(HttpStatusCode.Redirect, loginAfter.StatusCode);
        Assert.DoesNotContain("error", loginAfter.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Confirm_WithBadToken_RedirectsToBadToken()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/account/confirm?token=definitely-not-a-valid-token");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=badtoken", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Authorize_WhenCookieUserMissing_RedirectsToLogin_NotServerError()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Регистрируемся и подтверждаем e-mail — теперь есть idp-сессия (cookie).
        var email = $"stale-{Guid.NewGuid():N}@test.local";
        await Register(client, email);
        await client.GetAsync($"/account/confirm?token={ConfirmToken(email)}"); // ставит cookie idp_sso

        // Удаляем пользователя — cookie указывает на несуществующего.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Users.Remove(await db.Users.FirstAsync(u => u.Email == email));
            await db.SaveChangesAsync();
        }

        var verifier = "test-verifier-0123456789-0123456789-0123456789";
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = Uri.EscapeDataString("https://localhost:5001/signin-oidc");
        var authorizeUrl =
            $"/connect/authorize?client_id=chessschool-web&redirect_uri={redirectUri}" +
            $"&response_type=code&scope=openid&code_challenge={challenge}&code_challenge_method=S256";

        var response = await client.GetAsync(authorizeUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location!.OriginalString);
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> Register(HttpClient client, string email) =>
        client.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Test User",
            ["email"] = email,
            ["password"] = "secret123",
            ["return"] = "/"
        }));

    private static Task<HttpResponseMessage> Login(HttpClient client, string email) =>
        client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "secret123",
            ["return"] = "/"
        }));

    // Достаёт сырой токен подтверждения из последнего письма для адреса.
    private string ConfirmToken(string email)
    {
        var html = _factory.Sent.Last(m => m.To == email).Html;
        var m = Regex.Match(html, @"token=([A-Za-z0-9_\-]+)");
        Assert.True(m.Success, "в письме не найден токен подтверждения");
        return m.Groups[1].Value;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class AuthFactory(string connectionString) : WebApplicationFactory<ChessSchool.Auth.AuthMarker>
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:authdb"] = connectionString,
                ["Sso:Clients:chessschool-web"] = "https://localhost:5001"
            }));
            return base.CreateHost(builder);
        }

        // Перехватываем письма вместо реальной отправки — чтобы читать ссылку подтверждения.
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new CapturingEmailSender(Sent));
            });
    }

    private sealed class CapturingEmailSender(List<(string, string, string)> sink) : IEmailSender
    {
        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        {
            lock (sink) sink.Add((to, subject, htmlBody));
            return Task.CompletedTask;
        }
    }
}
