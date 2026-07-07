using ChessSchool.Arena.Services;
using ChessSchool.Contracts;

namespace ChessSchool.Arena.Grains;

/// <summary>
/// Каталог турниров (синглтон, ключ 0): синтезирует будущие слоты из расписания БЕЗ активации грейнов
/// и подмешивает живые (идущие/завершившиеся) через их грейны. Короткий кэш листинга гасит наплыв заходов.
/// </summary>
public sealed class ArenaDirectoryGrain(IGrainFactory grains) : Grain, IArenaDirectoryGrain
{
    // Короткий кэш листинга по sub. Грейн не-реентрантный → даже при наплыве заходов веер грейн-вызовов
    // выполняется один раз на окно TTL (первый ждёт, остальные queue → попадают в готовый кэш). Анонимные
    // landing-заходы делят запись sub="" (горячий путь); счётчики на главной устаревают максимум на TTL — ок.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, (DateTimeOffset Exp, IReadOnlyList<TournamentSummaryDto> List)> _cache = new();

    public async Task<IReadOnlyList<TournamentSummaryDto>> ListAsync(string? sub = null)
    {
        var now = DateTimeOffset.Now;

        var key = sub ?? "";
        if (_cache.TryGetValue(key, out var hit) && now < hit.Exp) return hit.List;

        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset)
            .AddHours(-ArenaSchedule.WindowBackHours);
        var windowEnd = windowStart.AddHours(ArenaSchedule.WindowBackHours + ArenaSchedule.WindowAheadHours);

        // Будущие турниры (ещё не начались) синтезируем из расписания БЕЗ активации грейна: у них
        // 0 игроков, а имя/контроль/длительность детерминированы из id. Окно — 6ч вперёд, поэтому это
        // большинство слотов; иначе каждый заход главной поднимал бы десятки холодных грейнов (медленный
        // TTFB, страница «висит» при переходе). Грейн зовём только для начавшихся (идут/завершились) —
        // там нужно живое состояние (счётчики/статус), и эти грейны обычно тёплые (идущие держат себя живыми).
        var future = new List<TournamentSummaryDto>();
        var liveIds = new List<string>();
        foreach (var spec in ArenaSchedule.Series)
            for (var t = windowStart.AddMinutes(spec.OffsetMin); t < windowEnd; t = t.AddMinutes(spec.StepMinutes))
            {
                var id = ArenaSchedule.MakeId(spec.Type, t);
                if (t > now)
                    future.Add(new TournamentSummaryDto(id, ArenaSchedule.MakeName(spec, t), spec.Tc,
                        TournamentStatus.Created, PlayerCount: 0, SecondsLeft: 0, BotCount: 0, t, spec.DurationSec));
                else
                    liveIds.Add(id);
            }

        // Передаём sub, чтобы отметить турниры, где участвует пользователь.
        var live = await Task.WhenAll(liveIds.Select(id =>
            grains.GetGrain<IArenaTournamentGrain>(id).PeekSummaryAsync(sub)));

        var list = future.Concat(live).OrderBy(t => t.StartsAt).ToList();

        _cache[key] = (now + CacheTtl, list);
        if (_cache.Count > 64) // чистим протухшие, чтобы кэш не рос по числу разных sub
            foreach (var k in _cache.Where(kv => kv.Value.Exp <= now).Select(kv => kv.Key).ToList())
                _cache.Remove(k);
        return list;
    }
}
