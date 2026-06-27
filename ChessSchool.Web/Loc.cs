using System.Globalization;

namespace ChessSchool.Web;

/// <summary>
/// Лёгкий локализатор RU/EN для веб-фронта (по образцу Arena). Культуру берёт из CurrentUICulture
/// (ставит RequestLocalization). Новые публичные страницы берут тексты отсюда (см. принцип в CLAUDE.md).
/// </summary>
public static class Loc
{
    public static bool IsEn => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en";

    private static readonly Dictionary<string, (string Ru, string En)> Ui = new()
    {
        ["nav.home"] = ("Главная", "Home"),
        ["nav.school"] = ("ЛК школы", "School area"),
        ["nav.attribution"] = ("Атрибуция", "Attribution"),
        ["nav.play"] = ("Играть", "Play"),
        ["auth.login"] = ("Войти", "Sign in"),

        ["home.pagetitle"] = ("ChessSchool — платформа для шахматных школ",
                             "ChessSchool — platform for chess schools"),
        ["home.metadesc"] = ("Платформа для шахматных школ: автоматический учёт партий, рейтинг ученика, прогресс для родителей и онлайн-игра.",
                            "A platform for chess schools: automatic game tracking, student rating, progress for parents and online play."),
        ["home.eyebrow"] = ("Платформа для шахматных школ", "Platform for chess schools"),
        ["home.h1"] = ("Прогресс каждого ученика — на ладони", "Every student's progress at a glance"),
        ["home.lead"] = ("Тренеры видят, как растёт рейтинг ребёнка, родители следят за успехами по ссылке, а ученики играют онлайн в реальном времени.",
                        "Coaches see how a child's rating grows, parents follow progress via a link, and students play online in real time."),
        ["home.cta.school"] = ("Открыть кабинет школы", "Open school area"),
        ["home.cta.play"] = ("Играть онлайн", "Play online"),

        ["home.c1.title"] = ("Личный кабинет школы", "School area"),
        ["home.c1.desc"] = ("Таблица учеников, рейтинг Glicko-2, статистика партий и побед.",
                           "Student roster, Glicko-2 rating, game and win statistics."),
        ["home.c1.link"] = ("Открыть →", "Open →"),
        ["home.c2.title"] = ("Онлайн-партии", "Online games"),
        ["home.c2.desc"] = ("Игра в реальном времени на Orleans + SignalR — под десятки тысяч партий.",
                           "Real-time play on Orleans + SignalR — built for tens of thousands of games."),
        ["home.c2.link"] = ("Играть →", "Play →"),
        ["home.c3.title"] = ("Для родителей", "For parents"),
        ["home.c3.desc"] = ("Тренер делится ссылкой на профиль ребёнка — без регистрации.",
                           "A coach shares a link to the child's profile — no sign-up needed."),
        ["home.c3.link"] = ("Атрибуция партий →", "Game attribution →"),
    };

    public static string T(string key) =>
        Ui.TryGetValue(key, out var v) ? (IsEn ? v.En : v.Ru) : key;
}
