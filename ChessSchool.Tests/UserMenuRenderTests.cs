using Bunit;
using ChessSchool.Design;

namespace ChessSchool.Tests;

/// <summary>
/// Меню пользователя (общий компонент Design): премиум-индикатор показывается только при Premium=true —
/// корона на аватаре и метка «Premium» в выпадающем меню. Чужой статус не светим (см. SUBSCRIPTIONS.md).
/// </summary>
public class UserMenuRenderTests : BunitContext
{
    [Fact]
    public void Premium_ShowsCrownAndBadge()
    {
        var cut = Render<UserMenu>(p => p
            .Add(c => c.Name, "Иван")
            .Add(c => c.Premium, true));

        Assert.Contains("um is-prem", cut.Markup);               // золотое кольцо на аватаре (класс корня)
        Assert.Contains("class=\"um-crown\"", cut.Markup);       // корона в углу (элемент, не CSS)
        Assert.Contains("class=\"um-prem\"", cut.Markup);        // метка в меню
    }

    [Fact]
    public void NonPremium_NoIndicator()
    {
        var cut = Render<UserMenu>(p => p
            .Add(c => c.Name, "Иван")
            .Add(c => c.Premium, false));

        Assert.DoesNotContain("class=\"um-crown\"", cut.Markup);
        Assert.DoesNotContain("class=\"um-prem\"", cut.Markup);
        Assert.DoesNotContain("um is-prem", cut.Markup);
    }
}
