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

    // ---- IdpUrl: единая точка построения браузерных ссылок на IdP (не из сырого Config["Sso:Authority"]) ----

    [Fact]
    public void IdpUrl_UsesServiceDiscovery_WhenSsoAuthorityEmpty() // главный сценарий бага (локально)
    {
        var c = Cfg(("services:auth:https:0", "https://auth.local"));
        Assert.Equal("https://auth.local/account/email", c.IdpUrl("/account/email"));
    }

    [Fact]
    public void IdpUrl_IsAbsolute_NotRelative_WhenAuthorityPresent()
    {
        var url = Cfg(("Sso:Authority", "https://id.example.com/")).IdpUrl("/account/email");
        Assert.StartsWith("https://id.example.com/account/email", url);
        Assert.DoesNotContain("//account", url); // хвостовой слэш authority не задваивается
    }

    [Fact]
    public void IdpUrl_AppendsEscapedReturn()
    {
        var url = Cfg(("Sso:Authority", "https://id.example.com")).IdpUrl("/account/email", "https://arena.local/premium");
        Assert.Equal("https://id.example.com/account/email?return=https%3A%2F%2Farena.local%2Fpremium", url);
    }
}
