using System.Security.Cryptography;
using System.Text;
using ChessSchool.ApiService.Services.Billing;
using ChessSchool.Contracts;

namespace ChessSchool.Tests;

/// <summary>
/// Вебхук Paddle Billing: верификация подписи Paddle-Signature (HMAC-SHA256 "ts:body", анти-replay)
/// и разбор события subscription.* в нормализованное BillingEventDto.
/// </summary>
public class PaddleWebhookTests
{
    private const string Secret = "pdl_ntfset_test_secret";

    private static string Sign(string body, long ts, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var h1 = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}:{body}")));
        return $"ts={ts};h1={h1}";
    }

    [Fact]
    public void ValidSignature_Accepted()
    {
        var now = DateTimeOffset.UtcNow;
        const string body = """{"event_id":"evt_1"}""";
        var header = Sign(body, now.ToUnixTimeSeconds(), Secret);
        Assert.True(PaddleWebhook.VerifySignature(body, header, Secret, now));
    }

    [Fact]
    public void TamperedBody_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var header = Sign("""{"event_id":"evt_1"}""", now.ToUnixTimeSeconds(), Secret);
        Assert.False(PaddleWebhook.VerifySignature("""{"event_id":"HACKED"}""", header, Secret, now));
    }

    [Fact]
    public void WrongSecret_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        const string body = """{"event_id":"evt_1"}""";
        var header = Sign(body, now.ToUnixTimeSeconds(), "other-secret");
        Assert.False(PaddleWebhook.VerifySignature(body, header, Secret, now));
    }

    [Fact]
    public void StaleTimestamp_Rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddHours(-1);
        const string body = """{"event_id":"evt_1"}""";
        var header = Sign(body, old.ToUnixTimeSeconds(), Secret);
        Assert.False(PaddleWebhook.VerifySignature(body, header, Secret, now)); // вне допуска свежести
    }

    [Fact]
    public void MalformedHeader_Rejected()
    {
        Assert.False(PaddleWebhook.VerifySignature("{}", "garbage", Secret, DateTimeOffset.UtcNow));
        Assert.False(PaddleWebhook.VerifySignature("{}", null, Secret, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Parse_SubscriptionActivated_MapsFields()
    {
        const string body = """
        {
          "event_id": "evt_123",
          "event_type": "subscription.activated",
          "data": {
            "id": "sub_abc",
            "status": "active",
            "customer_id": "ctm_xyz",
            "current_billing_period": { "ends_at": "2030-01-31T00:00:00Z" },
            "items": [ { "price": { "id": "pri_01kw6rax9s5bfx03vyk5ccgnbz" } } ],
            "custom_data": { "user_sub": "user-42" }
          }
        }
        """;

        Assert.True(PaddleWebhook.TryParse(body, out var ev));
        Assert.NotNull(ev);
        Assert.Equal("evt_123", ev!.EventId);
        Assert.Equal("user-42", ev.UserSub);
        Assert.Equal(SubscriptionStatus.Active, ev.Status);
        Assert.Equal("sub_abc", ev.ProviderSubscriptionId);
        Assert.Equal("ctm_xyz", ev.ProviderCustomerId);
        Assert.Equal("pri_01kw6rax9s5bfx03vyk5ccgnbz", ev.PriceId);
        Assert.Equal(new DateTimeOffset(2030, 1, 31, 0, 0, 0, TimeSpan.Zero), ev.CurrentPeriodEnd);
    }

    [Fact]
    public void Parse_Canceled_MapsStatus()
    {
        const string body = """
        {"event_id":"e","event_type":"subscription.canceled",
         "data":{"id":"sub_1","status":"canceled","custom_data":{"user_sub":"u"}}}
        """;
        Assert.True(PaddleWebhook.TryParse(body, out var ev));
        Assert.Equal(SubscriptionStatus.Canceled, ev!.Status);
    }

    [Fact]
    public void Parse_NonSubscriptionEvent_Ignored()
    {
        const string body = """{"event_id":"e","event_type":"transaction.completed","data":{"id":"txn_1"}}""";
        Assert.False(PaddleWebhook.TryParse(body, out _));
    }

    [Fact]
    public void Parse_MissingUserSub_Ignored()
    {
        const string body = """{"event_id":"e","event_type":"subscription.activated","data":{"id":"s","status":"active"}}""";
        Assert.False(PaddleWebhook.TryParse(body, out _)); // нет user_sub — некого премировать
    }
}
