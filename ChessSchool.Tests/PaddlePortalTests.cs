using ChessSchool.ApiService.Services.Billing;

namespace ChessSchool.Tests;

/// <summary>Customer Portal: разбор URL обзора из ответа Paddle portal-sessions и dev-заглушка.</summary>
public class PaddlePortalTests
{
    [Fact]
    public void ExtractOverviewUrl_FromPaddleResponse()
    {
        const string body = """
        {
          "data": {
            "id": "cpts_1",
            "urls": {
              "general": { "overview": "https://customer-portal.paddle.com/abc" },
              "subscriptions": [ { "id": "sub_1", "cancel_subscription": "https://..." } ]
            }
          }
        }
        """;
        Assert.Equal("https://customer-portal.paddle.com/abc", PaddleBillingProvider.ExtractOverviewUrl(body));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":{"urls":{}}}""")]
    [InlineData("not json")]
    public void ExtractOverviewUrl_ReturnsNull_OnMissingOrBad(string body)
        => Assert.Null(PaddleBillingProvider.ExtractOverviewUrl(body));

    [Fact]
    public async Task DevStub_HasNoPortal()
        => Assert.Null(await new DevStubBillingProvider().CreatePortalUrlAsync("ctm_x"));
}
