using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace ChessSchool.Tests;

/// <summary>
/// Rate-limiting IdP: письмо-эндпоинты (resend/register/change-email) защищены от email-бомбинга.
/// Лимит email-send выставлен маленьким (2/окно) — третий подряд запрос с того же клиента должен
/// получить 429. Требует Docker (Testcontainers), т.к. приложение при старте накатывает миграции.
/// </summary>
public class AuthRateLimitTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:18.3").Build();
    private WebApplicationFactory<ChessSchool.Auth.AuthMarker> _factory = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _factory = new Factory(_pg.GetConnectionString());
        _ = _factory.Services;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Resend_IsRateLimited_AfterThreshold()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        async Task<HttpStatusCode> Resend() =>
            (await client.PostAsync("/account/resend", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = "bombing-target@test.local",
                ["return"] = "/"
            }))).StatusCode;

        // Лимит email-send = 2/окно: первые два проходят (нейтральный редирект), третий — 429.
        Assert.Equal(HttpStatusCode.Redirect, await Resend());
        Assert.Equal(HttpStatusCode.Redirect, await Resend());
        Assert.Equal(HttpStatusCode.TooManyRequests, await Resend());
    }

    private sealed class Factory(string connectionString) : WebApplicationFactory<ChessSchool.Auth.AuthMarker>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:authdb"] = connectionString,
                ["RateLimit:Email:Permit"] = "2",
                ["RateLimit:Email:WindowMinutes"] = "5"
            }));
            return base.CreateHost(builder);
        }
    }
}
