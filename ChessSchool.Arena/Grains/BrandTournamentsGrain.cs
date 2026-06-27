using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Grains;

/// <summary>
/// Каталог бренд-турниров — единственный грейн (ключ 0). Единственный владелец списка в кластере
/// (правки из админки без гонок); состояние персистится в grain storage «arena» (Redis в проде).
/// Это только метаданные/расписание бренд-турниров; сами турниры — отдельные грейны (ключ = slug),
/// конфигурируемые из этих записей через IArenaTournamentGrain.ConfigureBrandAsync.
/// </summary>
public interface IBrandTournamentsGrain : IGrainWithIntegerKey
{
    Task<IReadOnlyList<BrandTournament>> GetAllAsync();
    Task<BrandTournament?> GetAsync(string slug);
    Task<BrandTournament> UpsertAsync(BrandTournament item);
    Task<bool> DeleteAsync(string slug);
    Task<bool> SetVisibleAsync(string slug, bool visible);
}

public sealed class BrandTournamentsGrain(
    [PersistentState("brand-tournaments", "arena")] IPersistentState<BrandTournamentsState> store,
    ILogger<BrandTournamentsGrain> logger) : Grain, IBrandTournamentsGrain
{
    public Task<IReadOnlyList<BrandTournament>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<BrandTournament>>(store.State.Items.Select(b => b.Clone()).ToList());

    public Task<BrandTournament?> GetAsync(string slug) =>
        Task.FromResult(store.State.Items.FirstOrDefault(b => b.Slug == slug)?.Clone());

    public async Task<BrandTournament> UpsertAsync(BrandTournament item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Slug);
        var saved = item.Clone();
        var idx = store.State.Items.FindIndex(b => b.Slug == saved.Slug);
        if (idx >= 0) store.State.Items[idx] = saved;
        else store.State.Items.Add(saved);
        await store.WriteStateAsync();
        logger.LogInformation("Бренд-турнир сохранён: {Slug}.", saved.Slug);
        return saved.Clone();
    }

    public async Task<bool> DeleteAsync(string slug)
    {
        var removed = store.State.Items.RemoveAll(b => b.Slug == slug) > 0;
        if (removed) await store.WriteStateAsync();
        return removed;
    }

    public async Task<bool> SetVisibleAsync(string slug, bool visible)
    {
        var item = store.State.Items.FirstOrDefault(b => b.Slug == slug);
        if (item is null) return false;
        item.Visible = visible;
        await store.WriteStateAsync();
        return true;
    }
}
