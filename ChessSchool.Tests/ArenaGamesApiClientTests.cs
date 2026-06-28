using ChessSchool.Arena.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessSchool.Tests;

/// <summary>
/// Клиент истории партий деградирует, а не падает, когда ApiService недоступен (отказ соединения/
/// таймаут): список → пустой, деталь/кэш разбора → null. Иначе страница «Мои партии» не открылась бы.
/// </summary>
public class ArenaGamesApiClientTests
{
    // Обработчик, имитирующий недоступный ApiService (как при запуске одного Arena без AppHost).
    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler) { BaseAddress = new Uri("https://apiservice") };
    }

    private static ArenaGamesApiClient ClientThatFailsWith(Exception ex) =>
        new(new StubFactory(new ThrowingHandler(ex)), "dev-internal-key",
            NullLogger<ArenaGamesApiClient>.Instance);

    [Fact]
    public async Task List_WhenApiServiceDown_ReturnsEmpty_NotThrows()
    {
        var client = ClientThatFailsWith(new HttpRequestException("connection refused"));
        var page = await client.ListAsync("sub", 0, 20, CancellationToken.None);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Get_WhenApiServiceTimesOut_ReturnsNull_NotThrows()
    {
        var client = ClientThatFailsWith(new TaskCanceledException("timeout"));
        Assert.Null(await client.GetAsync(Guid.NewGuid(), "sub", CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellation_IsPropagated_NotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = ClientThatFailsWith(new OperationCanceledException(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ListAsync("sub", 0, 20, cts.Token));
    }
}
