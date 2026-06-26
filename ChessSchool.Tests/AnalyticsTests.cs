using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChessSchool.Tests;

/// <summary>Проверяет switchable-выбор провайдера аналитики по конфигурации.</summary>
public class AnalyticsTests
{
    private static IAnalytics Resolve(Dictionary<string, string?> cfg)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(cfg);
        builder.AddChessSchoolAnalytics();
        return builder.Build().Services.GetRequiredService<IAnalytics>();
    }

    [Fact]
    public void WithoutApiKey_UsesNoop()
    {
        Assert.IsType<NoopAnalytics>(Resolve(new()));
    }

    [Fact]
    public void WithApiKey_UsesRealProvider()
    {
        var analytics = Resolve(new() { ["Analytics:PostHog:ApiKey"] = "phc_test_key" });
        Assert.IsNotType<NoopAnalytics>(analytics);
        // Не должен бросать при capture (fire-and-forget, ошибки сети глотаются).
        analytics.Capture("test_event", "user-1");
    }
}
