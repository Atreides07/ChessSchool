using Microsoft.Extensions.Logging;

namespace ChessSchool.Tests;

public class WebTests
{
    // Холодный старт поднимает контейнер Postgres (при первом прогоне ещё и тянется образ ~479MB) +
    // 4 сервиса + Orleans-силос. Таймаут с запасом на первую загрузку образа (с кэшем прогон ~30с).
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(300);

    [Fact]
    public async Task GetWebResourceRootReturnsOkStatusCode()
    {
        // Arrange
        var cancellationToken = new CancellationTokenSource(DefaultTimeout).Token;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ChessSchool_AppHost>(cancellationToken);
        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            // Override the logging filters from the app's configuration
            logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire.", LogLevel.Debug);
            // To output logs to the xUnit.net ITestOutputHelper, consider adding a package from https://www.nuget.org/packages?q=xunit+logging
        });
        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        await using var app = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await app.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);

        // Act — ждём, пока веб-ресурс перейдёт в Running (вся цепочка WaitFor: auth → apiservice → gameserver → web).
        var httpClient = app.CreateHttpClient("webfrontend");
        await app.ResourceNotifications.WaitForResourceAsync("webfrontend", KnownResourceStates.Running, cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);
        // Прогрев: первый запрос платит одноразовый JIT/прогрев конвейера Blazor SSR (замер: cold ~4.5с,
        // warm ~7мс; под нагрузкой параллельных тестов cold может пробить 30s-лимит resilience-хэндлера у
        // app.CreateHttpClient → флак). Дожимаем первый ответ плоским клиентом с щедрым таймаутом, чтобы
        // ассерты шли по «тёплому» процессу. См. грабля #8.
        await WarmUpAsync(httpClient.BaseAddress!, cancellationToken);
        var response = await httpClient.GetAsync("/", cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Страница турнира Arena — статический SSR тонкого клиента (без Blazor-circuit): каркас + конфиг
        // + клиентский скрипт; доступна анонимно (зрители). Проверяем, что миграция не сломала отдачу.
        var arena = app.CreateHttpClient("arena");
        await app.ResourceNotifications
            .WaitForResourceAsync("arena", KnownResourceStates.Running, cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);
        await WarmUpAsync(arena.BaseAddress!, cancellationToken); // прогрев JIT Arena до ассертов (см. выше)

        // Home Arena (расписание + лента «Главные турниры») — SSR читает каталог бренд-турниров.
        var arenaHome = await arena.GetAsync("/", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, arenaHome.StatusCode);

        var tournamentPage = await arena.GetAsync("/t/test-tournament", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, tournamentPage.StatusCode);
        var html = await tournamentPage.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("id=\"t-root\"", html);
        Assert.Contains("js/tournament.js", html);

        // Хаб турнира смапплен и доступен анонимно (зрители подключаются без входа).
        var negotiate = await arena.PostAsync("/arenahub/negotiate?negotiateVersion=1", null, cancellationToken);
        Assert.NotEqual(HttpStatusCode.NotFound, negotiate.StatusCode);

        // «Напомнить» (.ics) смапплен и гейтит небрендовые турниры (test-tournament — не бренд → 404).
        var ics = await arena.GetAsync("/t/test-tournament/calendar.ics", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, ics.StatusCode);
    }

    // Дожимает первый (холодный) ответ сервиса плоским клиентом с щедрым таймаутом — вне 30s-лимита
    // resilience-хэндлера, которым обёрнуты клиенты из app.CreateHttpClient. Так первичный JIT конвейера
    // происходит здесь, а не под тайт-таймаутом ассерта. Ограничен внешним cancellationToken (300с).
    private static async Task WarmUpAsync(Uri baseAddress, CancellationToken ct)
    {
        using var raw = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(120) };
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var r = await raw.GetAsync("/", ct);
                if (r.IsSuccessStatusCode) return;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* сервис ещё прогревается — повторим */ }
            await Task.Delay(500, ct);
        }
    }
}
