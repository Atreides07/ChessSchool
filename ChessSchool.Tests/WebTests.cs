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
        var response = await httpClient.GetAsync("/", cancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Страница турнира Arena — статический SSR тонкого клиента (без Blazor-circuit): каркас + конфиг
        // + клиентский скрипт; доступна анонимно (зрители). Проверяем, что миграция не сломала отдачу.
        var arena = app.CreateHttpClient("arena");
        await app.ResourceNotifications
            .WaitForResourceAsync("arena", KnownResourceStates.Running, cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);

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
    }
}
