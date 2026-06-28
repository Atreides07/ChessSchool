using System.Net;
using System.Text;
using ChessSchool.ApiService.Services.Billing;
using ChessSchool.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessSchool.Tests;

/// <summary>
/// Reconcile через Paddle API: проброс user_sub из транзакции в подписку (Paddle хранит customData на
/// транзакции, не на подписке) и восстановление статуса по e-mail клиента (когда строки подписки нет/
/// txn потерян — кнопка «Обновить статус»). Имитируем ответы Paddle stub-обработчиком HttpClient.
/// </summary>
public class PaddleBillingProviderTests
{
    // Отдаёт канонический JSON по пути запроса (path+query содержит ключ из карты).
    private sealed class RouteHandler(Func<string, string?> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = route(request.RequestUri!.PathAndQuery);
            var resp = body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            return Task.FromResult(resp);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("https://sandbox-api.paddle.com") };
    }

    private static PaddleBillingProvider Provider(Func<string, string?> route) =>
        new(new PaddleOptions { ApiKey = "pdl_test_key", Environment = "sandbox" },
            new StubFactory(new RouteHandler(route)), NullLogger<PaddleBillingProvider>.Instance);

    [Fact]
    public async Task FetchByTransaction_PropagatesUserSubFromTransaction_ToSubscriptionWithoutCustomData()
    {
        // Транзакция несёт custom_data.user_sub и subscription_id; сама подписка БЕЗ custom_data.
        var provider = Provider(path =>
        {
            if (path.StartsWith("/transactions/")) return """
                {"data":{"id":"txn_1","subscription_id":"sub_1","customer_id":"ctm_1",
                 "custom_data":{"user_sub":"user-123"}}}
                """;
            if (path.StartsWith("/subscriptions/sub_1")) return """
                {"data":{"id":"sub_1","status":"active","customer_id":"ctm_1",
                 "current_billing_period":{"ends_at":"2031-01-01T00:00:00Z"},"items":[{"price":{"id":"pri_x"}}]}}
                """;
            return null;
        });

        var ev = await provider.FetchByTransactionAsync("txn_1");
        Assert.NotNull(ev);
        Assert.Equal("user-123", ev!.UserSub);   // взят из транзакции, хотя у подписки его нет
        Assert.Equal(SubscriptionStatus.Active, ev.Status);
        Assert.Equal("sub_1", ev.ProviderSubscriptionId);
    }

    [Fact]
    public async Task FetchByCustomerEmail_FindsActiveSubscription_ForLoggedInUser()
    {
        // «Обновить статус» без txn и без нашей строки: ищем клиента по e-mail, берём его активную подписку.
        var provider = Provider(path =>
        {
            if (path.StartsWith("/customers?email=")) return """{"data":[{"id":"ctm_9"}]}""";
            if (path.StartsWith("/subscriptions?customer_id=ctm_9")) return """
                {"data":[{"id":"sub_9","status":"active","customer_id":"ctm_9",
                 "current_billing_period":{"ends_at":"2031-06-01T00:00:00Z"}}]}
                """;
            return null;
        });

        var ev = await provider.FetchByCustomerEmailAsync("buyer@example.com", "user-9");
        Assert.NotNull(ev);
        Assert.Equal("user-9", ev!.UserSub);     // связали найденную подписку с залогиненным пользователем
        Assert.Equal(SubscriptionStatus.Active, ev.Status);
        Assert.Equal("sub_9", ev.ProviderSubscriptionId);
    }

    [Fact]
    public async Task FetchByCustomerEmail_NoCustomer_ReturnsNull()
    {
        var provider = Provider(_ => """{"data":[]}"""); // клиента с таким e-mail нет
        Assert.Null(await provider.FetchByCustomerEmailAsync("nobody@example.com", "user-x"));
    }
}
