using Bunit;
using ChessSchool.Arena.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ChessSchool.Tests;

/// <summary>
/// Страница «Все игры» переведена на тонкий клиент: статический SSR-каркас без серверной Blazor-цепи,
/// без грейна и без IJSRuntime на рендере (доски рисует games.js по SignalR). Тест доказывает, что
/// страница рендерится с минимальным набором сервисов (нет зависимости от грейна/циркита) и отдаёт
/// контейнер #ag-root с атрибутами, на которые опирается клиентский JS.
/// </summary>
public class AllGamesRenderTests : BunitContext
{
    [Fact]
    public void RendersStaticSkeleton_WithoutGrainOrCircuit()
    {
        // @Assets[...] инъектится фреймворком — регистрируем пустую коллекцию (неизвестный ключ
        // возвращается как есть). Грейн/IJSRuntime НЕ регистрируем: если бы страница их требовала
        // на рендере (как старый InteractiveServer-вариант), тест упал бы здесь.
        Services.AddSingleton(new ResourceAssetCollection([]));

        var cut = Render<AllGames>(p => p.Add(c => c.Id, "boards-test"));

        var html = cut.Markup;

        // Контейнер тонкого клиента с данными для games.js.
        Assert.Contains("id=\"ag-root\"", html);
        Assert.Contains("data-id=\"boards-test\"", html);
        Assert.Contains("data-signalr=", html);

        // Каркас навигации и заголовок приходят в HTML сразу (первый кадр без JS).
        Assert.Contains("ag-title", html);
        Assert.Contains("/t/boards-test", html); // хлебная крошка обратно на турнир

        // Локализованные строки для клиента отданы инлайн-JSON.
        Assert.Contains("ag-loc", html);
        Assert.Contains("onboard", html);

        // Никаких следов серверной интерактивной доски (её рендерит JS на клиенте).
        Assert.DoesNotContain("blazor-internal-error", html);
    }
}
