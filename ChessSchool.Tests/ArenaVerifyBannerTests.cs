using Bunit;
using Bunit.TestDoubles;
using ChessSchool.Arena.Components.Layout;
using ChessSchool.Arena.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace ChessSchool.Tests;

/// <summary>
/// Регрессия: nudge-баннер мягкого гейта («Подтвердите e-mail») должен вести на IdP (Auth), а не на хост
/// самой Арены. Баг: MainLayout брал голый Config["Sso:Authority"] (локально ПУСТ) → ссылка становилась
/// относительной (/account/email) и открывалась на хосте Арены, где такого маршрута нет → Not Found, письмо
/// не уходило. Фикс — резолвить хост IdP через ResolveSsoAuthority (как OIDC-конвейер: service discovery).
/// </summary>
public class ArenaVerifyBannerTests : BunitContext
{
    private sealed class FakeEntitlements : IPlayerEntitlements
    {
        public Task<bool> IsPremiumAsync(string? sub, CancellationToken ct = default) => Task.FromResult(false);
        public void Invalidate(string? sub) { }
    }

    [Fact]
    public void VerifyBanner_ManageEmailLink_PointsToAbsoluteIdpAuthority()
    {
        // Вошёл, но e-mail НЕ подтверждён → баннер виден. email_verified claim отсутствует.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "u1"), new Claim("email", "u@test.local")], authenticationType: "test"));
        Services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
        // Sso:Authority НЕ задан (как локально) — хост IdP приходит из service discovery Aspire.
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["services:auth:https:0"] = "https://auth.local" }).Build());
        Services.AddSingleton<IPlayerEntitlements>(new FakeEntitlements());
        this.AddAuthorization(); // MainLayout содержит AuthorizeView

        var cut = Render<MainLayout>(p => p.Add(c => c.Body, "<p>body</p>"));

        var href = cut.Find(".ar-verify a").GetAttribute("href")!;
        // Абсолютная ссылка на IdP, а не относительная «/account/email» (которая вела бы на хост Арены → Not Found).
        Assert.StartsWith("https://auth.local/account/email", href);
    }
}
