namespace ChessSchool.Arena;

/// <summary>Известный мировой турнир (для страниц /majors и /majors/{slug}).</summary>
public sealed record Major(
    string Slug, string Name, string Series, string SeriesCls,
    DateOnly Start, DateOnly End, string City, string Country, string Flag, string Format, string Url);

/// <summary>
/// Справочник топ-турниров сезона (проверено по календарю FIDE/Grand Chess Tour/Wikipedia, июнь 2026).
/// Общий источник для списка, детальных страниц и sitemap — единые данные, slug = стабильный URL.
/// </summary>
public static class ArenaMajors
{
    public static readonly Major[] All =
    [
        new("superunited-rapid-blitz-croatia-2026", "SuperUnited Rapid & Blitz Croatia", "Grand Chess Tour", "gct",
            new(2026, 6, 29), new(2026, 7, 6), "Загреб", "Хорватия", "🇭🇷", "Рапид и блиц", "https://grandchesstour.org"),
        new("biel-chess-festival-2026", "Biel Chess Festival", "Классика", "cls",
            new(2026, 7, 11), new(2026, 7, 24), "Биль", "Швейцария", "🇨🇭", "Классика · 2700+", "https://www.bielchessfestival.ch"),
        new("chennai-grand-masters-2026", "Chennai Grand Masters", "Классика", "cls",
            new(2026, 7, 15), new(2026, 7, 23), "Ченнаи", "Индия", "🇮🇳", "Классика · круговой", "https://en.wikipedia.org/wiki/2026_in_chess"),
        new("saint-louis-rapid-blitz-2026", "Saint Louis Rapid & Blitz", "Grand Chess Tour", "gct",
            new(2026, 7, 31), new(2026, 8, 7), "Сент-Луис", "США", "🇺🇸", "Рапид и блиц", "https://grandchesstour.org"),
        new("sinquefield-cup-2026", "Sinquefield Cup", "Grand Chess Tour", "gct",
            new(2026, 8, 8), new(2026, 8, 21), "Сент-Луис", "США", "🇺🇸", "Классика · элита", "https://grandchesstour.org"),
        new("cairns-cup-2026", "Cairns Cup", "Женский элит", "wom",
            new(2026, 8, 8), new(2026, 8, 21), "Сент-Луис", "США", "🇺🇸", "Классика · круговой", "https://saintlouischessclub.org"),
        new("gct-finals-2026", "GCT Finals", "Grand Chess Tour", "gct",
            new(2026, 8, 21), new(2026, 8, 28), "Сент-Луис", "США", "🇺🇸", "Плей-офф · топ-4", "https://grandchesstour.org"),
    ];

    private static readonly string[] MonthsShortRu =
        ["", "янв.", "фев.", "мар.", "апр.", "мая", "июня", "июля", "авг.", "сент.", "окт.", "нояб.", "дек."];
    private static readonly string[] MonthsShortEn =
        ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] MonthsGenRu =
        ["", "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
    private static readonly string[] MonthsFullEn =
        ["", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static string[] MonthsShort => Loc.IsEn ? MonthsShortEn : MonthsShortRu;

    /// <summary>Не завершившиеся турниры в хронологическом порядке.</summary>
    public static IEnumerable<Major> Upcoming()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return All.Where(t => t.End >= today).OrderBy(t => t.Start);
    }

    public static Major? BySlug(string slug) => All.FirstOrDefault(m => m.Slug == slug);

    /// <summary>«29 июня – 6 июля» / «11–24 июля» (короткий месяц).</summary>
    public static string DateRange(DateOnly a, DateOnly b) =>
        a.Month == b.Month
            ? $"{a.Day}–{b.Day} {MonthsShort[b.Month]}"
            : $"{a.Day} {MonthsShort[a.Month]} – {b.Day} {MonthsShort[b.Month]}";

    /// <summary>Полная дата «29 июня 2026» / «June 29, 2026».</summary>
    public static string LongDate(DateOnly d) =>
        Loc.IsEn ? $"{MonthsFullEn[d.Month]} {d.Day}, {d.Year}" : $"{d.Day} {MonthsGenRu[d.Month]} {d.Year}";

    // Возвращает CSS-класс, вид статуса (для локализации) и число дней до старта. Текст даёт Loc.StatusLabel.
    public static (string Cls, string Kind, int Days) Status(Major t)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today < t.Start)
        {
            var days = t.Start.DayNumber - today.DayNumber;
            return days <= 1 ? ("soon", "tomorrow", days)
                : days <= 14 ? ("soon", "soon", days)
                : ("later", "later", days);
        }
        return today <= t.End ? ("live", "live", 0) : ("done", "done", 0);
    }
}
