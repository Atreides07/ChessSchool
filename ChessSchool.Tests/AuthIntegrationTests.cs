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
/// Интеграционные тесты IdP против реального PostgreSQL (Testcontainers). Покрывают: JWKS без приватного
/// материала; МЯГКИЙ гейт (регистрация сразу логинит и шлёт письмо; логин пускает неподтверждённого);
/// смену e-mail до подтверждения (исправить опечатку); погашение токена; протухшую cookie. Письма
/// перехватывает фейковый <see cref="IEmailSender"/>. Требует Docker.
/// </summary>
public class AuthIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:18.3").Build();
    private AuthFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _factory = new AuthFactory(_pg.GetConnectionString());
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

        Assert.True(keys.GetArrayLength() >= 1);
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
    public async Task Register_SignsInImmediately_UserUnconfirmed_EmailSent()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"reg-{Guid.NewGuid():N}@test.local";

        var register = await Register(client, email);

        // Мягкий гейт: пускаем сразу (redirect в приложение, ставится cookie idp), но e-mail не подтверждён.
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        Assert.Equal("/", register.Headers.Location!.OriginalString);
        Assert.Contains(register.Headers.GetValues("Set-Cookie"), c => c.StartsWith("idp_sso"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.False((await db.Users.FirstAsync(u => u.Email == email)).EmailConfirmed);
        }

        var msg = Assert.Single(_factory.Sent, m => m.To == email);
        Assert.Contains("token=", msg.Html);
    }

    [Fact]
    public async Task Login_Succeeds_EvenWhenEmailUnconfirmed()
    {
        var reg = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"soft-{Guid.NewGuid():N}@test.local";
        await Register(reg, email);

        // Свежий клиент (без cookie): вход неподтверждённого проходит (мягкий гейт), без ошибки.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Login(client, email);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.DoesNotContain("error", login.Headers.Location!.OriginalString);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("idp_sso"));
    }

    [Fact]
    public async Task ChangeEmail_BeforeConfirmation_UpdatesAddress_ResendsAndInvalidatesOldToken()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var oldEmail = $"old-{Guid.NewGuid():N}@test.local";
        var newEmail = $"new-{Guid.NewGuid():N}@test.local";

        await Register(client, oldEmail);           // вошли (мягкий гейт), письмо на старый адрес
        var oldToken = ConfirmToken(oldEmail);

        // Опечатка → меняем адрес до подтверждения (клиент аутентифицирован cookie от register).
        var change = await client.PostAsync("/account/change-email", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = newEmail,
            ["return"] = "/"
        }));
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True(await db.Users.AnyAsync(u => u.Email == newEmail));
            Assert.False(await db.Users.AnyAsync(u => u.Email == oldEmail));
        }

        // Старая ссылка больше не работает, новая — подтверждает.
        var newToken = ConfirmToken(newEmail);
        Assert.Contains("error=badtoken", (await client.GetAsync($"/account/confirm?token={oldToken}")).Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/account/confirm?token={newToken}")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True((await db.Users.FirstAsync(u => u.Email == newEmail)).EmailConfirmed);
        }
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

        // Регистрация сразу логинит (мягкий гейт) → есть idp-сессия (cookie).
        var email = $"stale-{Guid.NewGuid():N}@test.local";
        await Register(client, email);

        // Удаляем пользователя — cookie указывает на несуществующего.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Users.Remove(await db.Users.FirstAsync(u => u.Email == email));
            await db.SaveChangesAsync();
        }

        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes("test-verifier-0123456789-0123456789-0123456789")));
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
