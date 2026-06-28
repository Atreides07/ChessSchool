using ChessSchool.Arena.Services;
using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Grains;

/// <summary>
/// Настройки ботов — единственный грейн (ключ 0). Хранит желаемое число ботов по типу регулярного
/// турнира (Bullet/Blitz/Rapid). Единственный владелец в кластере (правки из админки без гонок),
/// состояние персистится в grain storage «arena» (Redis в проде). Грейн турнира читает это число для
/// своего типа и поддерживает столько ботов в идущем турнире (0 — без ботов).
/// </summary>
public interface IBotSettingsGrain : IGrainWithIntegerKey
{
    /// <summary>Число ботов по каждому типу расписания (с учётом значения по умолчанию для незаданных).</summary>
    Task<IReadOnlyDictionary<string, int>> GetAllAsync();
    /// <summary>Число ботов для типа (значение по умолчанию, если не задано). Тип вне расписания → 0.</summary>
    Task<int> GetCountAsync(string type);
    Task SetCountAsync(string type, int count);
}

[GenerateSerializer]
public sealed class BotSettingsState
{
    [Id(0)] public Dictionary<string, int> CountByType { get; set; } = new();
}

public sealed class BotSettingsGrain(
    [PersistentState("bot-settings", "arena")] IPersistentState<BotSettingsState> store,
    ILogger<BotSettingsGrain> logger) : Grain, IBotSettingsGrain
{
    public const int DefaultCount = 6; // дефолт по типу, пока админ не задал своё
    private const int MaxCount = 50;   // здравый предел

    public Task<IReadOnlyDictionary<string, int>> GetAllAsync()
    {
        var result = new Dictionary<string, int>();
        foreach (var spec in ArenaSchedule.Series)
            result[spec.Type] = store.State.CountByType.TryGetValue(spec.Type, out var n) ? n : DefaultCount;
        return Task.FromResult<IReadOnlyDictionary<string, int>>(result);
    }

    public Task<int> GetCountAsync(string type)
    {
        if (ArenaSchedule.Series.All(s => s.Type != type)) return Task.FromResult(0); // вне расписания — без ботов
        return Task.FromResult(store.State.CountByType.TryGetValue(type, out var n) ? n : DefaultCount);
    }

    public async Task SetCountAsync(string type, int count)
    {
        if (ArenaSchedule.Series.All(s => s.Type != type))
            throw new ArgumentException($"Неизвестный тип турнира: {type}", nameof(type));
        store.State.CountByType[type] = Math.Clamp(count, 0, MaxCount);
        await store.WriteStateAsync();
        logger.LogInformation("Число ботов для {Type} = {Count}.", type, store.State.CountByType[type]);
    }
}
