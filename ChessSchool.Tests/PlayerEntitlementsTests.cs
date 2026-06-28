using System.Net;
using System.Net.Http.Json;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessSchool.Tests;

/// <summary>
/// Entitlement игрока в Arena: премиум-статус из ApiService, кэш на ноду, безопасная деградация
/// (ApiService недоступен → НЕ премиум, платное не открываем бесплатно).
/// </summary>
public class PlayerEntitlementsTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static PlayerEntitlements Make(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://apiservice") };
        return new PlayerEntitlements(new SingleClientFactory(client),
            new MemoryCache(new MemoryCacheOptions()), "internal-key",
            NullLogger<PlayerEntitlements>.Instance);
    }

    private static HttpResponseMessage Sub(bool premium) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new SubscriptionDto("u",
                premium ? SubscriptionStatus.Active : SubscriptionStatus.None,
                premium ? "premium" : null, null, premium))
        };

    [Fact]
    public async Task Premium_WhenApiSaysActive()
        => Assert.True(await Make(new StubHandler(_ => Sub(true))).IsPremiumAsync("u"));

    [Fact]
    public async Task NotPremium_WhenApiSaysNone()
        => Assert.False(await Make(new StubHandler(_ => Sub(false))).IsPremiumAsync("u"));

    [Fact]
    public async Task Result_IsCached_PerSub()
    {
        var h = new StubHandler(_ => Sub(true));
        var svc = Make(h);
        await svc.IsPremiumAsync("u");
        await svc.IsPremiumAsync("u");
        Assert.Equal(1, h.Calls); // второй раз — из кэша
    }

    [Fact]
    public async Task Invalidate_DropsCache_Refetches()
    {
        // После reconcile/активации кэш сбрасывается — следующий запрос идёт в ApiService заново.
        var h = new StubHandler(_ => Sub(true));
        var svc = Make(h);
        await svc.IsPremiumAsync("u");
        svc.Invalidate("u");
        await svc.IsPremiumAsync("u");
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public async Task Degrades_ToFalse_OnErrorStatus()
        => Assert.False(await Make(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).IsPremiumAsync("u"));

    [Fact]
    public async Task Degrades_ToFalse_OnException()
        => Assert.False(await Make(new StubHandler(_ => throw new HttpRequestException("down"))).IsPremiumAsync("u"));

    [Fact]
    public async Task EmptySub_IsFalse_WithoutCall()
    {
        var h = new StubHandler(_ => Sub(true));
        Assert.False(await Make(h).IsPremiumAsync(null));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task SendsInternalKey_AndSubPath()
    {
        HttpRequestMessage? seen = null;
        var svc = Make(new StubHandler(r => { seen = r; return Sub(true); }));
        await svc.IsPremiumAsync("user-7");
        Assert.NotNull(seen);
        Assert.Contains("/internal/subscriptions/user-7", seen!.RequestUri!.ToString());
        Assert.Equal("internal-key", seen.Headers.GetValues("X-Internal-Key").Single());
    }
}
