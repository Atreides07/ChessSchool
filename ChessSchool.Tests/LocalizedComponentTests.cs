using System.Globalization;
using Bunit;
using ChessSchool.Arena;
using ChessSchool.Arena.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace ChessSchool.Tests;

/// <summary>
/// Регрессия «прыгающей» локализации: интерактивный компонент ре-рендерится из фонового колбэка
/// (таймер/нотификация) на потоке с дефолтной культурой. <see cref="LocalizedComponentBase"/> должен
/// восстанавливать захваченную при инициализации культуру, иначе UI мерцает ru↔en.
/// </summary>
public class LocalizedComponentTests : BunitContext
{
    private sealed class CultureProbe : LocalizedComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
            => builder.AddContent(0, Loc.IsEn ? "EN" : "RU");

        public Task BackgroundRenderAsync() => InvokeStateHasChangedLocalizedAsync();
    }

    [Fact]
    public async Task BackgroundRender_KeepsCapturedCulture_NoFlicker()
    {
        var original = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            // Инициализация под культурой запроса (ru) — компонент её захватывает.
            var ru = new CultureInfo("ru");
            CultureInfo.CurrentCulture = ru;
            CultureInfo.CurrentUICulture = ru;
            var cut = Render<CultureProbe>();
            Assert.Equal("RU", cut.Markup);

            // Фоновый колбэк приходит на потоке с дефолтной культурой (en) — без фикса рендер был бы "EN".
            var en = new CultureInfo("en");
            CultureInfo.CurrentCulture = en;
            CultureInfo.CurrentUICulture = en;
            await cut.Instance.BackgroundRenderAsync();

            Assert.Equal("RU", cut.Markup); // культура восстановлена → мерцания нет
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = original;
        }
    }
}
