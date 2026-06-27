using System.Net;
using System.Text;
using ChessSchool.Arena.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessSchool.Tests;

/// <summary>
/// Перенос внешних URL изображений в наше хранилище: пропуск уже-локальных, SSRF-защита (приватные
/// адреса), скачивание и сохранение, отказ для не-картинок, следование редиректам.
/// </summary>
public class ImageIngestorTests
{
    // Хранилище, которое «настроено» и при сохранении отдаёт ссылку на /media с ключом по типу.
    private sealed class FakeStorage(bool configured) : IImageStorage
    {
        public bool IsConfigured => configured;
        public string? SavedContentType;
        public long SavedBytes;

        public async Task<string> SaveAsync(Stream content, string contentType, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            SavedBytes = ms.Length;
            SavedContentType = contentType;
            return $"/media/broadcasts/{Guid.NewGuid():N}.{ImageKinds.Extension(contentType)}";
        }

        public Task<ImageContent?> OpenAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ImageContent?>(null);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request, _calls++));
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Ингестор с принудительно «разрешённым» хостом — изолирует оркестрацию от реального DNS/сети.
    private sealed class AllowAllIngestor(IImageStorage storage, IHttpClientFactory http)
        : ImageIngestor(storage, http, NullLogger<ImageIngestor>.Instance)
    {
        protected override ValueTask<bool> IsHostAllowedAsync(Uri uri, CancellationToken ct) =>
            ValueTask.FromResult(true);
    }

    private static HttpResponseMessage Image(string contentType, byte[] bytes)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return resp;
    }

    private static IImageIngestor Ingestor(IImageStorage storage, Func<HttpRequestMessage, int, HttpResponseMessage> respond) =>
        new AllowAllIngestor(storage, new FakeHttpClientFactory(new StubHandler(respond)));

    [Fact]
    public async Task NotConfigured_KeepsExternalUrl()
    {
        var ing = Ingestor(new FakeStorage(configured: false), (_, _) => throw new Exception("не должно качать"));
        var url = "https://example.com/banner.webp";
        Assert.Equal(url, await ing.EnsureStoredAsync(url));
    }

    [Theory]
    [InlineData("/media/broadcasts/abc.webp")] // относительная — уже наша
    [InlineData("")]
    [InlineData(null)]
    public async Task LocalOrEmpty_ReturnedUnchanged(string? url)
    {
        var ing = Ingestor(new FakeStorage(configured: true), (_, _) => throw new Exception("не должно качать"));
        Assert.Equal(url, await ing.EnsureStoredAsync(url));
    }

    [Fact]
    public async Task ExternalImage_IsDownloadedAndStored()
    {
        var storage = new FakeStorage(configured: true);
        var bytes = Encoding.ASCII.GetBytes("fake-webp-bytes");
        var ing = Ingestor(storage, (_, _) => Image("image/webp", bytes));

        var result = await ing.EnsureStoredAsync(
            "https://media.idchess.com/_next/image?url=https%3A%2F%2Fs3.idsport.tech%2Fb.webp&w=640&q=75");

        Assert.StartsWith("/media/broadcasts/", result);
        Assert.EndsWith(".webp", result);
        Assert.Equal("image/webp", storage.SavedContentType);
        Assert.Equal(bytes.Length, storage.SavedBytes);
    }

    [Fact]
    public async Task NonImageContentType_Throws()
    {
        var ing = Ingestor(new FakeStorage(configured: true),
            (_, _) => Image("text/html", Encoding.ASCII.GetBytes("<html>")));

        await Assert.ThrowsAsync<ImageIngestException>(() =>
            ing.EnsureStoredAsync("https://example.com/not-image"));
    }

    [Fact]
    public async Task FollowsRedirect_ToImage()
    {
        var storage = new FakeStorage(configured: true);
        var ing = Ingestor(storage, (req, call) => call == 0
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://cdn.example.com/final.png") } }
            : Image("image/png", Encoding.ASCII.GetBytes("png")));

        var result = await ing.EnsureStoredAsync("https://example.com/start.png");
        Assert.EndsWith(".png", result);
    }

    [Fact]
    public async Task PrivateHost_IsRejected_BySsrfGuard()
    {
        // Реальный гард (без override): localhost резолвится в loopback и должен быть отклонён.
        var ing = new ImageIngestor(new FakeStorage(configured: true),
            new FakeHttpClientFactory(new StubHandler((_, _) => Image("image/png", [1, 2, 3]))),
            NullLogger<ImageIngestor>.Instance);

        await Assert.ThrowsAsync<ImageIngestException>(() =>
            ing.EnsureStoredAsync("http://localhost:9999/x.png"));
    }
}
