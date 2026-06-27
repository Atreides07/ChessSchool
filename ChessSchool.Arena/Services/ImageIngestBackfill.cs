using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Разовый фоновый перенос УЖЕ сохранённых внешних URL изображений (бренд-турниры и трансляции) в наше
/// S3-хранилище. Нужен для записей, созданных до появления переноса при сохранении: иначе главная грузит
/// фон с внешнего источника (медленно и хрупко — его могут подменить/удалить). Идемпотентно: /media-ссылки
/// пропускаются, поэтому при последующих стартах (когда всё уже перенесено) работы нет.
/// Запускается только при настроенном S3; ошибки отдельных записей не валят процесс (best-effort).
/// </summary>
public sealed class ImageIngestBackfill(
    IImageStorage storage,
    IImageIngestor ingestor,
    BroadcastsCatalog broadcasts,
    BrandTournamentCatalog brands,
    ILogger<ImageIngestBackfill> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured) return; // dev без S3 — переносить некуда, внешние ссылки остаются как есть

        // Не на самом старте: даём подняться кластеру Orleans (каталоги ходят в грейны). Best-effort.
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (OperationCanceledException) { return; }

        try { await RunOnceAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogWarning(ex, "Бэкафилл изображений в S3 прерван."); }
    }

    /// <summary>Один проход переноса (выделено для тестов). Возвращает число перенесённых изображений.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var moved = 0;

        foreach (var b in await brands.AllFreshAsync())
        {
            if (ct.IsCancellationRequested) break;
            if (!IsExternal(b.ImageUrl)) continue;
            if (await TryIngestAsync(b.ImageUrl!, b.Slug, ct) is { } stored)
            {
                var clone = b.Clone();
                clone.ImageUrl = stored;
                await brands.UpsertAsync(clone);
                moved++;
            }
        }

        foreach (var t in await broadcasts.AllFreshAsync())
        {
            if (ct.IsCancellationRequested) break;
            if (!IsExternal(t.ImageUrl)) continue;
            if (await TryIngestAsync(t.ImageUrl!, t.Slug, ct) is { } stored)
            {
                var clone = t.Clone();
                clone.ImageUrl = stored;
                await broadcasts.UpsertAsync(clone);
                moved++;
            }
        }

        if (moved > 0) log.LogInformation("Перенесено внешних фоновых изображений в S3: {Count}.", moved);
        return moved;
    }

    // Скачиваем и сохраняем; null — если перенос не дал новой (нашей) ссылки или упал (оставляем как было).
    private async Task<string?> TryIngestAsync(string url, string slug, CancellationToken ct)
    {
        try
        {
            var stored = await ingestor.EnsureStoredAsync(url, ct);
            return stored != url ? stored : null;
        }
        catch (ImageIngestException ex)
        {
            log.LogWarning("Не удалось перенести фон '{Slug}' в S3: {Message}", slug, ex.Message);
            return null;
        }
    }

    private static bool IsExternal(string? url) =>
        url is not null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
