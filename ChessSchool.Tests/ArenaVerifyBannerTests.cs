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

    // Рендерит MainLayout под пользователем с указанными claim'ами (кроме sub/email).
    private IRenderedComponent<MainLayout> RenderAs(params Claim[] extra)
    {
        Claim[] claims = [new("sub", "u1"), new("email", "u@test.local"), .. extra];
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
        Services.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
        // Sso:Authority НЕ задан (как локально) — хост IdP приходит из service discovery Aspire.
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["services:auth:https:0"] = "https://auth.local" }).Build());
        Services.AddSingleton<IPlayerEntitlements>(new FakeEntitlements());
        this.AddAuthorization(); // MainLayout содержит AuthorizeView
        return Render<MainLayout>(p => p.Add(c => c.Body, "<p>body</p>"));
    }

    [Fact]
    public void VerifyBanner_ManageEmailLink_PointsToAbsoluteIdpAuthority()
    {
        // Вошёл, но e-mail НЕ подтверждён (claim отсутствует) → баннер виден.
        var cut = RenderAs();
        var href = cut.Find(".ar-verify a").GetAttribute("href")!;
        // Абсолютная ссылка на IdP, а не относительная «/account/email» (которая вела бы на хост Арены → Not Found).
        Assert.StartsWith("https://auth.local/account/email", href);
    }

    [Theory]
    [InlineData("True")]  // как OIDC сериализует БУЛЕВ claim (JsonElement.ToString()) — главный сценарий бага
    [InlineData("true")]  // как строку кладёт id-токен
    public void VerifyBanner_Hidden_WhenEmailVerified_RegardlessOfCase(string value)
    {
        // email_verified присутствует и истинный → баннера мягкого гейта быть НЕ должно.
        var cut = RenderAs(new Claim("email_verified", value));
        Assert.Empty(cut.FindAll(".ar-verify"));
    }
}
