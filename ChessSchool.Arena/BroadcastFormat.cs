namespace ChessSchool.Arena;

/// <summary>
/// Локализованное форматирование дат и статуса трансляции (для карточек, детальных страниц, sitemap).
/// Чистые функции без состояния — данные приходят из каталога (<see cref="Broadcast"/>).
/// </summary>
public static class BroadcastFormat
{
    private static readonly string[] MonthsShortRu =
        ["", "янв.", "фев.", "мар.", "апр.", "мая", "июня", "июля", "авг.", "сент.", "окт.", "нояб.", "дек."];
    private static readonly string[] MonthsShortEn =
        ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] MonthsGenRu =
        ["", "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
    private static readonly string[] MonthsFullEn =
        ["", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static string[] MonthsShort => Loc.IsEn ? MonthsShortEn : MonthsShortRu;

    /// <summary>Видимые трансляции в хронологическом порядке (для публичных страниц/sitemap).</summary>
    public static IEnumerable<Broadcast> Public(IEnumerable<Broadcast> all) =>
        all.Where(b => b.Visible).OrderBy(b => b.Start).ThenBy(b => b.Name);

    /// <summary>«29 июня – 6 июля» / «11–24 июля» (короткий месяц).</summary>
    public static string DateRange(DateOnly a, DateOnly b) =>
        a.Month == b.Month
            ? $"{a.Day}–{b.Day} {MonthsShort[b.Month]}"
            : $"{a.Day} {MonthsShort[a.Month]} – {b.Day} {MonthsShort[b.Month]}";

    /// <summary>Полная дата «29 июня 2026» / «June 29, 2026».</summary>
    public static string LongDate(DateOnly d) =>
        Loc.IsEn ? $"{MonthsFullEn[d.Month]} {d.Day}, {d.Year}" : $"{d.Day} {MonthsGenRu[d.Month]} {d.Year}";

    // Транслитерация кириллицы → латиница (ГОСТ-подобная), чтобы русские названия давали читаемый slug.
    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a",
        ['б'] = "b",
        ['в'] = "v",
        ['г'] = "g",
        ['д'] = "d",
        ['е'] = "e",
        ['ё'] = "e",
        ['ж'] = "zh",
        ['з'] = "z",
        ['и'] = "i",
        ['й'] = "y",
        ['к'] = "k",
        ['л'] = "l",
        ['м'] = "m",
        ['н'] = "n",
        ['о'] = "o",
        ['п'] = "p",
        ['р'] = "r",
        ['с'] = "s",
        ['т'] = "t",
        ['у'] = "u",
        ['ф'] = "f",
        ['х'] = "kh",
        ['ц'] = "ts",
        ['ч'] = "ch",
        ['ш'] = "sh",
        ['щ'] = "shch",
        ['ъ'] = "",
        ['ы'] = "y",
        ['ь'] = "",
        ['э'] = "e",
        ['ю'] = "yu",
        ['я'] = "ya",
    };

    /// <summary>
    /// URL-идентификатор из названия: транслитерация кириллицы → латиница, нижний регистр,
    /// пробелы/символы → дефисы. «Шахматный турнир Бристоль» → «shakhmatnyy-turnir-bristol».
    /// </summary>
    public static string Slugify(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length * 2);
        bool prevDash = false;
        foreach (var raw in s.Trim().ToLowerInvariant())
        {
            // Кириллицу разворачиваем в латиницу, остальное обрабатываем посимвольно.
            var chunk = Translit.TryGetValue(raw, out var lat) ? lat : raw.ToString();
            foreach (var ch in chunk)
            {
                if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); prevDash = false; }
                else if (!prevDash && sb.Length > 0) { sb.Append('-'); prevDash = true; }
            }
        }
        return sb.ToString().Trim('-');
    }

    private static readonly System.Text.RegularExpressions.Regex SlugRx =
        new("^[a-z0-9]+(?:-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Проверка корректности идентификатора (для валидации формы админки).</summary>
    public static bool IsValidSlug(string s) => !string.IsNullOrEmpty(s) && SlugRx.IsMatch(s);

    // CSS-класс, вид статуса (для локализации) и число дней до старта. Текст даёт Loc.StatusLabel.
    public static (string Cls, string Kind, int Days) Status(Broadcast t)
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
