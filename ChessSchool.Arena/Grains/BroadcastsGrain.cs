using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Grains;

/// <summary>
/// Каталог трансляций — единственный грейн (ключ 0). Единственный владелец контента в кластере
/// (однопоточный доступ → нет гонок при правках из админки на любой ноде). Состояние персистится
/// в grain storage «arena» (Redis в проде → переживает рестарт/масштабирование силосов; память в dev).
/// </summary>
public interface IBroadcastsGrain : IGrainWithIntegerKey
{
    Task<IReadOnlyList<Broadcast>> GetAllAsync();
    Task<Broadcast?> GetAsync(string slug);
    /// <summary>Создать или обновить трансляцию (ключ — Slug). Возвращает сохранённую версию.</summary>
    Task<Broadcast> UpsertAsync(Broadcast item);
    /// <summary>Удалить по slug. true — если запись существовала.</summary>
    Task<bool> DeleteAsync(string slug);
    /// <summary>Скрыть/показать. true — если запись найдена.</summary>
    Task<bool> SetVisibleAsync(string slug, bool visible);
}

public sealed class BroadcastsGrain(
    [PersistentState("broadcasts", "arena")] IPersistentState<BroadcastsState> store,
    ILogger<BroadcastsGrain> logger) : Grain, IBroadcastsGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Первая активация: заливаем стартовый набор. Флаг Seeded гарантирует, что сид не перетрёт
        // последующие правки админа (даже если он удалит все записи — повторного сида не будет).
        if (!store.State.Seeded)
        {
            store.State.Items = BroadcastSeed.Initial.Select(b => b.Clone()).ToList();
            store.State.Seeded = true;
            store.State.SeedVersion = BroadcastSeed.Version;
            await store.WriteStateAsync();
            logger.LogInformation("Каталог трансляций инициализирован стартовым набором ({Count}).", store.State.Items.Count);
        }
        else if (store.State.SeedVersion < BroadcastSeed.Version)
        {
            await ReconcileSeedAsync();
        }
        await base.OnActivateAsync(cancellationToken);
    }

    /// <summary>
    /// Дозаливка новых полей сида в уже засеянный каталог (повышение версии). Заполняет ТОЛЬКО пустые
    /// значения у существующих по slug записей — правки админа (непустые поля) не трогаются. Идемпотентно.
    /// Сейчас касается только изображений (v1 → v2).
    /// </summary>
    private async Task ReconcileSeedAsync()
    {
        int filled = 0;
        foreach (var seed in BroadcastSeed.Initial)
        {
            var existing = store.State.Items.FirstOrDefault(b => b.Slug == seed.Slug);
            if (existing is null) continue;
            if (string.IsNullOrWhiteSpace(existing.ImageUrl) && !string.IsNullOrWhiteSpace(seed.ImageUrl))
            {
                existing.ImageUrl = seed.ImageUrl;
                filled++;
            }
        }
        store.State.SeedVersion = BroadcastSeed.Version;
        await store.WriteStateAsync();
        logger.LogInformation("Каталог трансляций обновлён до версии сида {Version}: дозалито изображений {Filled}.",
            BroadcastSeed.Version, filled);
    }

    public Task<IReadOnlyList<Broadcast>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<Broadcast>>(store.State.Items.Select(b => b.Clone()).ToList());

    public Task<Broadcast?> GetAsync(string slug) =>
        Task.FromResult(store.State.Items.FirstOrDefault(b => b.Slug == slug)?.Clone());

    public async Task<Broadcast> UpsertAsync(Broadcast item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Slug);
        var saved = item.Clone();
        var idx = store.State.Items.FindIndex(b => b.Slug == saved.Slug);
        if (idx >= 0) store.State.Items[idx] = saved;
        else store.State.Items.Add(saved);
        await store.WriteStateAsync();
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
