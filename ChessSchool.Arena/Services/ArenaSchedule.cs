using ChessSchool.Contracts;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Единое расписание арен. И каталог (генерация слотов), и грейн (самоконфигурация из id ссылки)
/// используют один источник, поэтому мета турнира выводится из его id детерминированно — даже если
/// грейн деактивировался и был поднят заново при прямом переходе на /t/{id}.
/// </summary>
public static class ArenaSchedule
{
    /// <summary>Повторяющаяся серия: тип, контроль, шаг (мин), длительность (сек), смещение старта (мин).</summary>
    public sealed record Spec(string Type, TimeControl Tc, int StepMinutes, int DurationSec, int OffsetMin);

    // Регулярные турниры идут плотно, встык: длительность ≈ шагу, поэтому турниры одного типа не
    // перекрываются (важно и для таймлайна — карточки занимают столбцы по длительности).
    public static readonly Spec[] Series =
    [
        new("Bullet", new TimeControl(60, 0), 30, 1800, 0),   // каждые 30 мин, 30 мин
        new("Blitz", new TimeControl(180, 0), 60, 3600, 0),   // каждый час, 60 мин
        new("Rapid", new TimeControl(600, 0), 60, 3600, 30),  // каждый час, 60 мин, старт в :30
    ];

    public const int WindowBackHours = 3;
    public const int WindowAheadHours = 6;

    /// <summary>Id слота = "{type}-{unixStart}" (нижний регистр типа).</summary>
    public static string MakeId(string type, DateTimeOffset startsAt) =>
        $"{type.ToLowerInvariant()}-{startsAt.ToUnixTimeSeconds()}";

    public static string MakeName(Spec spec, DateTimeOffset startsAt) =>
        $"{spec.Type} {spec.Tc} {startsAt.ToLocalTime():HH:mm}";

    /// <summary>Канонический тип регулярного турнира по его id (Bullet/Blitz/Rapid). null — id вне расписания.</summary>
    public static string? TypeOf(string id)
    {
        var dash = id.LastIndexOf('-');
        if (dash <= 0) return null;
        var type = id[..dash];
        return Series.FirstOrDefault(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Type;
    }

    /// <summary>Разбирает id ссылки в мету турнира. null — id не относится к расписанию (напр. тестовый).</summary>
    public static (string Name, TimeControl Tc, DateTimeOffset StartsAt, int DurationSeconds)? Resolve(string id)
    {
        var dash = id.LastIndexOf('-');
        if (dash <= 0 || !long.TryParse(id.AsSpan(dash + 1), out var unix)) return null;

        var type = id[..dash];
        var spec = Series.FirstOrDefault(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        if (spec is null) return null;

        var startsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        return (MakeName(spec, startsAt), spec.Tc, startsAt, spec.DurationSec);
    }
}
