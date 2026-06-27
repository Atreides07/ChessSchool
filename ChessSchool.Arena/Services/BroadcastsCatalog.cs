using ChessSchool.Arena.Grains;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Локальный (пер-нодовый) ускоритель над грейном-каталогом трансляций. Публичная страница
/// /broadcasts — горячий путь: гонять каждый SSR-рендер в единственный грейн каталога (возможно, на
/// другой ноде) — узкое место под нагрузкой. Поэтому ноды держат снимок с коротким TTL поверх общего
/// источника истины (грейн + Redis grain storage). Источник истины — грейн; кэш лишь снижает число
/// обращений и обязан переживать потерю любой ноды (так и есть — он восстановим из грейна).
///
/// Запись (из админки) идёт write-through в грейн и сразу инвалидирует локальный кэш; кэши других нод
/// сходятся к новому состоянию по истечении TTL (для контент-списка задержка в секунды приемлема).
/// Регистрируется синглтоном (один снимок на ноду).
/// </summary>
public sealed class BroadcastsCatalog(IGrainFactory grains)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<Broadcast> _snapshot = [];
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    private IBroadcastsGrain Grain => grains.GetGrain<IBroadcastsGrain>(0);

    /// <summary>Все трансляции (кэш с TTL). Используется и публичной частью, и админкой для чтения.</summary>
    public async Task<IReadOnlyList<Broadcast>> AllAsync()
    {
        if (DateTimeOffset.UtcNow < _expiresAt) return _snapshot;
        await _gate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow < _expiresAt) return _snapshot; // другой поток уже обновил
            _snapshot = await Grain.GetAllAsync();
            _expiresAt = DateTimeOffset.UtcNow + Ttl;
            return _snapshot;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Видимые трансляции в хронологическом порядке (публичные страницы и sitemap).</summary>
    public async Task<IReadOnlyList<Broadcast>> PublicAsync() =>
        BroadcastFormat.Public(await AllAsync()).ToList();

    /// <summary>Одна трансляция по slug из кэша (детальная страница).</summary>
    public async Task<Broadcast?> BySlugAsync(string slug) =>
        (await AllAsync()).FirstOrDefault(b => b.Slug == slug);

    /// <summary>Свежий список напрямую из грейна (для админки — без задержки кэша).</summary>
    public Task<IReadOnlyList<Broadcast>> AllFreshAsync()
    {
        Invalidate();
        return Grain.GetAllAsync();
    }

    public async Task<Broadcast> UpsertAsync(Broadcast item)
    {
        var saved = await Grain.UpsertAsync(item);
        Invalidate();
        return saved;
    }

    public async Task<bool> DeleteAsync(string slug)
    {
        var ok = await Grain.DeleteAsync(slug);
        Invalidate();
        return ok;
    }

    public async Task<bool> SetVisibleAsync(string slug, bool visible)
    {
        var ok = await Grain.SetVisibleAsync(slug, visible);
        Invalidate();
        return ok;
    }

    private void Invalidate() => _expiresAt = DateTimeOffset.MinValue;
}
