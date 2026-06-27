using System.Globalization;

namespace ChessSchool.Arena;

/// <summary>
/// Лёгкий локализатор RU/EN. Культуру берёт из <see cref="CultureInfo.CurrentUICulture"/> (её ставит
/// RequestLocalization по запросу). <see cref="T"/> — UI-строки по ключу; <see cref="Tr"/> — перевод
/// значений данных (русский текст → английский), чтобы не дублировать справочник турниров.
/// Новые страницы должны брать тексты отсюда (см. принцип локализации в CLAUDE.md).
/// </summary>
public static class Loc
{
    public static bool IsEn => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en";

    private static readonly Dictionary<string, (string Ru, string En)> Ui = new()
    {
        ["nav.arena"] = ("Турниры «Арена»", "Arena Tournaments"),
        ["nav.majors"] = ("Турниры", "Tournaments"),
        ["search.placeholder"] = ("Поиск по турнирам", "Search tournaments"),
        ["auth.login"] = ("Войти", "Sign in"),
        ["majors.title"] = ("Известные турниры", "Notable tournaments"),
        ["majors.sub"] = ("Топ-события мировых шахмат на ближайшие два месяца. Время местное у организаторов.",
                          "Top world chess events for the next two months. Local time at the organizers."),
        ["majors.pagetitle"] = ("Турниры мировых шахмат — расписание | ChessArena",
                               "World chess tournaments — schedule | ChessArena"),
        ["majors.more"] = ("Подробнее →", "Details →"),
        ["majors.metaprefix"] = ("Расписание известных мировых шахматных турниров на ближайшие два месяца:",
                                "Schedule of notable world chess tournaments for the next two months:"),
        ["majors.metasuffix"] = ("и другие. Даты, города, форматы.", "and more. Dates, cities, formats."),
        ["detail.dates"] = ("Даты", "Dates"),
        ["detail.place"] = ("Место", "Location"),
        ["detail.format"] = ("Формат", "Format"),
        ["detail.series"] = ("Серия", "Series"),
        ["detail.site"] = ("Сайт организатора ↗", "Organizer's site ↗"),
        ["detail.all"] = ("← Все турниры", "← All tournaments"),
        ["detail.notfound"] = ("Турнир не найден.", "Tournament not found."),
        ["crumb.home"] = ("Главная", "Home"),
        ["crumb.majors"] = ("Турниры", "Tournaments"),
    };

    // Перевод значений справочника (хранится по-русски) на английский.
    private static readonly Dictionary<string, string> Data = new()
    {
        ["Классика"] = "Classical",
        ["Женский элит"] = "Women's elite",
        ["Рапид и блиц"] = "Rapid & blitz",
        ["Классика · 2700+"] = "Classical · 2700+",
        ["Классика · круговой"] = "Classical · round-robin",
        ["Классика · элита"] = "Classical · elite",
        ["Плей-офф · топ-4"] = "Playoff · top 4",
        ["Загреб"] = "Zagreb",
        ["Биль"] = "Biel",
        ["Ченнаи"] = "Chennai",
        ["Сент-Луис"] = "Saint Louis",
        ["Хорватия"] = "Croatia",
        ["Швейцария"] = "Switzerland",
        ["Индия"] = "India",
        ["США"] = "USA",
    };

    public static string T(string key) =>
        Ui.TryGetValue(key, out var v) ? (IsEn ? v.En : v.Ru) : key;

    public static string Tr(string ruValue) =>
        IsEn && Data.TryGetValue(ruValue, out var en) ? en : ruValue;

    public static string StatusLabel(string kind, int days) => (kind, IsEn) switch
    {
        ("live", false) => "Идёт сейчас",
        ("live", true) => "Live now",
        ("tomorrow", false) => "Завтра",
        ("tomorrow", true) => "Tomorrow",
        ("soon", false) => $"Через {days} дн.",
        ("soon", true) => $"In {days} days",
        ("later", false) => "Скоро",
        ("later", true) => "Soon",
        (_, false) => "Завершён",
        (_, true) => "Finished",
    };
}
