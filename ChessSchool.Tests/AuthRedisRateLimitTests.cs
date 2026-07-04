using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ChessSchool.Tests;

/// <summary>
/// Rate-limiting через РАСПРЕДЕЛЁННЫЙ лимитер (Redis): при заданной строке подключения `redis` счётчик
/// живёт в Redis (общий для всех нод), а не в памяти процесса. Проверяем, что лимит реально энфорсится
/// этим путём (третий запрос → 429). Требует Docker (Postgres для миграций + Redis для лимитера).
/// </summary>
[Trait("Category", "Docker")]
public class AuthRedisRateLimitTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:18.3").Build();
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:8.6").Build();
    private WebApplicationFactory<ChessSchool.Auth.AuthMarker> _factory = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _redis.StartAsync());
        _factory = new Factory(_pg.GetConnectionString(), _redis.GetConnectionString());
        _ = _factory.Services;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await Task.WhenAll(_pg.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Resend_IsRateLimited_ViaRedis_AfterThreshold()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        async Task<HttpStatusCode> Resend() =>
            (await client.PostAsync("/account/resend", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = "redis-bombing-target@test.local",
                ["return"] = "/"
            }))).StatusCode;

        // Лимит email-send = 2/окно (в Redis): первые два проходят, третий — 429.
        Assert.Equal(HttpStatusCode.Redirect, await Resend());
        Assert.Equal(HttpStatusCode.Redirect, await Resend());
        Assert.Equal(HttpStatusCode.TooManyRequests, await Resend());
    }

    private sealed class Factory(string pgConn, string redisConn) : WebApplicationFactory<ChessSchool.Auth.AuthMarker>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:authdb"] = pgConn,
                ["ConnectionStrings:redis"] = redisConn, // включает распределённый путь лимитера
                ["RateLimit:Email:Permit"] = "2",
                ["RateLimit:Email:WindowMinutes"] = "5",
                ["Auth:Password:CheckPwned"] = "false"
            }));
            return base.CreateHost(builder);
        }
    }
}
