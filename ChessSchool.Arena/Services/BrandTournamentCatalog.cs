using ChessSchool.Arena.Grains;
using ChessSchool.Contracts;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Каталог бренд-турниров: пер-нодовый кэш с TTL поверх грейна-каталога (источник истины + Redis storage),
/// плюс реализация <see cref="IBrandTournaments"/> (решение об индексации/sitemap). При сохранении
/// конфигурирует грейн самого турнира из записи (ConfigureBrandAsync) — бренд-турнир становится реальным
/// играбельным грейном со стабильным slug. Запись инвалидирует локальный кэш; другие ноды сходятся по TTL.
/// </summary>
public sealed class BrandTournamentCatalog(IGrainFactory grains) : IBrandTournaments
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<BrandTournament> _snapshot = [];
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    private IBrandTournamentsGrain Grain => grains.GetGrain<IBrandTournamentsGrain>(0);

    public async Task<IReadOnlyList<BrandTournament>> AllAsync()
    {
        if (DateTimeOffset.UtcNow < _expiresAt) return _snapshot;
        await _gate.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow < _expiresAt) return _snapshot;
            _snapshot = await Grain.GetAllAsync();
            _expiresAt = DateTimeOffset.UtcNow + Ttl;
            return _snapshot;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BrandTournament>> VisibleAsync() =>
        (await AllAsync()).Where(b => b.Visible).OrderBy(b => b.StartsAt).ToList();

    public async Task<BrandTournament?> BySlugAsync(string slug) =>
        (await AllAsync()).FirstOrDefault(b => b.Slug == slug);

    public Task<IReadOnlyList<BrandTournament>> AllFreshAsync()
    {
        Invalidate();
        return Grain.GetAllAsync();
    }

    /// <summary>
    /// Видимые бренд-турниры + их живые сводки из грейнов. Порядок: идёт сейчас → ближайшие будущие →
    /// завершённые. Имя/описание/изображение берём из каталога (авторитет), статус/счётчики — из грейна.
    /// </summary>
    public async Task<IReadOnlyList<BrandTournamentView>> ListWithSummaryAsync(string? sub)
    {
        var brands = await VisibleAsync();
        var views = await Task.WhenAll(brands.Select(async b =>
            new BrandTournamentView(b, await grains.GetGrain<IArenaTournamentGrain>(b.Slug).GetSummaryAsync(sub))));
        return views
            .OrderBy(v => v.Summary.Status switch
            {
                TournamentStatus.Running => 0,
                TournamentStatus.Created => 1,
                _ => 2
            })
            .ThenBy(v => v.Brand.StartsAt)
            .ToList();
    }

    public async Task<BrandTournament> UpsertAsync(BrandTournament item)
    {
        var saved = await Grain.UpsertAsync(item);
        Invalidate();
        // Конфигурируем грейн самого турнира из записи (реальный играбельный турнир со стабильным slug).
        await grains.GetGrain<IArenaTournamentGrain>(saved.Slug).ConfigureBrandAsync(
            saved.Name, new TimeControl(saved.InitialSeconds, saved.IncrementSeconds), saved.StartsAt, saved.DurationSeconds);
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

    // --- IBrandTournaments (решение об индексации) ---

    public async Task<bool> IsBrandAsync(string id) =>
        (await AllAsync()).Any(b => b.Slug == id && b.Visible); // индексируем только видимые

    public async Task<IReadOnlyList<BrandTournamentRef>> ListIndexableAsync() =>
        (await VisibleAsync()).Select(b => new BrandTournamentRef(b.Slug)).ToList();

    private void Invalidate() => _expiresAt = DateTimeOffset.MinValue;
}
