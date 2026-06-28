using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting; // ResolveSsoAuthority — extension в namespace Microsoft.Extensions.Hosting

namespace ChessSchool.Tests;

/// <summary>
/// Разрешение authority IdP: явный Sso:Authority (нужен за публичным адресом — dev tunnel/прод)
/// перебивает service discovery Aspire; иначе берётся внутренний адрес; иначе null.
/// </summary>
public class SsoAuthorityResolutionTests
{
    private static IConfiguration Cfg(params (string Key, string? Value)[] kv) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(kv.ToDictionary(x => x.Key, x => x.Value))
            .Build();

    [Fact]
    public void ExplicitAuthority_Wins_OverServiceDiscovery()
    {
        var c = Cfg(("Sso:Authority", "https://auth-abc.devtunnels.ms"),
                    ("services:auth:https:0", "https://internal:7139"));
        Assert.Equal("https://auth-abc.devtunnels.ms", c.ResolveSsoAuthority());
    }

    [Fact]
    public void FallsBackTo_ServiceDiscovery_Https()
    {
        var c = Cfg(("services:auth:https:0", "https://internal:7139"));
        Assert.Equal("https://internal:7139", c.ResolveSsoAuthority());
    }

    [Fact]
    public void FallsBackTo_Http_WhenNoHttps()
    {
        var c = Cfg(("services:auth:http:0", "http://internal:5139"));
        Assert.Equal("http://internal:5139", c.ResolveSsoAuthority());
    }

    [Fact]
    public void Null_WhenNothingConfigured() => Assert.Null(Cfg().ResolveSsoAuthority());
}
