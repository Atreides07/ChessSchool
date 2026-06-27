using ChessSchool.Arena;
using ChessSchool.Arena.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace ChessSchool.Tests;

/// <summary>
/// Бэкафилл переносит уже сохранённые внешние URL изображений (бренд-турниры + трансляции) в S3
/// и идемпотентен: повторный проход ничего не делает (всё уже /media).
/// </summary>
public class ImageIngestBackfillTests
{
    private sealed class FakeEngine : IChessEngine
    {
        public Task<string?> GetBestMoveAsync(string fen, int skillLevel, int moveTimeMs, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("arena");
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.ConfigureServices(s =>
            {
                s.AddSingleton<ArenaNotifier>();
                s.AddSingleton<IChessEngine, FakeEngine>();
                s.AddSingleton(new ArenaRuntimeOptions(RemindersEnabled: false));
                s.AddSingleton<IAnalytics, NoopAnalytics>();
            });
        }
    }

    // Хранилище «настроено», но в RunOnceAsync напрямую не используется.
    private sealed class StubStorage : IImageStorage
    {
        public bool IsConfigured => true;
        public Task<string> SaveAsync(Stream content, string contentType, CancellationToken ct = default) =>
            Task.FromResult("/media/broadcasts/x.webp");
        public Task<ImageContent?> OpenAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ImageContent?>(null);
    }

    // Внешний http(s) → новая /media-ссылка; локальное/пустое — без изменений (как реальный ингестор).
    private sealed class FakeIngestor : IImageIngestor
    {
        public int Calls;
        public Task<string?> EnsureStoredAsync(string? url, CancellationToken ct = default)
        {
            var external = url is not null &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
            if (external) Calls++;
            return Task.FromResult<string?>(external ? $"/media/broadcasts/{Guid.NewGuid():N}.webp" : url);
        }
    }

    private static bool IsExternal(string? u) =>
        u is not null && (u.StartsWith("http://") || u.StartsWith("https://"));

    [Fact]
    public async Task Backfill_MovesExternalUrls_ToMedia_AndIsIdempotent()
    {
        var cluster = new TestClusterBuilder().AddSiloBuilderConfigurator<SiloConfigurator>().Build();
        await cluster.DeployAsync();
        try
        {
            var brands = new BrandTournamentCatalog(cluster.GrainFactory);
            var broadcasts = new BroadcastsCatalog(cluster.GrainFactory);

            // Бренд с внешним фоном (как запись, созданная до переноса при сохранении).
            await brands.UpsertAsync(new BrandTournament
            {
                Slug = "brand-ext",
                Name = "Бренд",
                ImageUrl = "https://media.idchess.com/_next/image?url=https%3A%2F%2Fs3%2Fb.webp&w=640&q=75",
                InitialSeconds = 180,
                StartsAt = DateTimeOffset.UtcNow.AddDays(7),
                DurationSeconds = 3600,
                Visible = true,
            });

            var ingestor = new FakeIngestor();
            var sut = new ImageIngestBackfill(new StubStorage(), ingestor, broadcasts, brands,
                NullLogger<ImageIngestBackfill>.Instance);

            // Сид трансляций приходит с внешними URL — их тоже должно перенести.
            var externalBefore = (await broadcasts.AllFreshAsync()).Count(b => IsExternal(b.ImageUrl));
            Assert.True(externalBefore > 0); // в сиде есть внешние ссылки

            var moved = await sut.RunOnceAsync();

            Assert.Equal(externalBefore + 1, moved); // трансляции из сида + 1 бренд
            Assert.DoesNotContain(await brands.AllFreshAsync(), b => IsExternal(b.ImageUrl));
            Assert.DoesNotContain(await broadcasts.AllFreshAsync(), b => IsExternal(b.ImageUrl));
            Assert.StartsWith("/media/", (await brands.BySlugAsync("brand-ext"))!.ImageUrl);

            // Идемпотентность: всё уже /media → второй проход ничего не переносит.
            Assert.Equal(0, await sut.RunOnceAsync());
        }
        finally { await cluster.StopAllSilosAsync(); }
    }
}
