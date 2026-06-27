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

        // --- Расписание (Home) ---
        ["loading"] = ("Загрузка…", "Loading…"),
        ["sched.title"] = ("Расписание турниров", "Tournament schedule"),
        ["sched.livenow"] = ("идёт сейчас", "live now"),
        ["view.timeline"] = ("Таймлайн", "Timeline"),
        ["view.list"] = ("Список", "List"),
        ["ctl.now"] = ("сейчас", "now"),
        ["ctl.back"] = ("Назад", "Back"),
        ["ctl.fwd"] = ("Вперёд", "Forward"),
        ["ctl.scale"] = ("Масштаб", "Scale"),
        ["mine.title"] = ("Вы участвуете", "You're participating"),
        ["mine.badge"] = ("★ вы участвуете", "★ you're in"),
        ["mine.you"] = ("★ вы", "★ you"),
        ["reg.short"] = ("запись", "sign up"),
        ["hint.live"] = ("Идёт сейчас — можно играть", "Live now — you can play"),
        ["hint.past"] = ("Завершён — результаты", "Finished — results"),
        ["hint.future"] = ("Регистрация открыта", "Registration open"),
        ["cat.running"] = ("Текущие", "Ongoing"),
        ["cat.next"] = ("Следующие", "Upcoming"),
        ["cat.finished"] = ("Завершённые", "Finished"),
        ["cat.upcoming"] = ("Предстоящие", "Upcoming"),
        ["empty.upcoming"] = ("Нет предстоящих турниров в ближайшие часы.", "No upcoming tournaments in the next few hours."),
        ["status.live"] = ("идёт", "live"),
        ["status.finished"] = ("завершён", "finished"),
        ["status.reg"] = ("регистрация", "registration"),
        ["unit.players"] = ("игроков", "players"),
        ["unit.bots"] = ("ботов", "bots"),

        // --- Турнир (Tournament) ---
        ["t.fallback"] = ("Турнир", "Tournament"),
        ["t.finished"] = ("Завершён", "Finished"),
        ["t.soon"] = ("Скоро", "Soon"),
        ["t.minutes"] = ("минут", "minutes"),
        ["t.control"] = ("Контроль", "Time control"),
        ["t.endin"] = ("Окончание через:", "Ends in:"),
        ["t.start"] = ("Старт:", "Start:"),
        ["waiting.search"] = ("Ищем соперника", "Looking for an opponent"),
        ["waiting.score"] = ("Ваши очки:", "Your score:"),
        ["game.waitnext"] = ("— ждём следующего соперника…", "— waiting for the next opponent…"),
        ["game.yourmove"] = ("Ваш ход", "Your move"),
        ["game.oppmove"] = ("Ход соперника — можно сделать предход", "Opponent's move — you can premove"),
        ["game.berserktip"] = ("Время пополам, без инкремента, +1 очко за победу", "Half the time, no increment, +1 point for a win"),
        ["game.resign"] = ("Сдаться", "Resign"),
        ["sec.games"] = ("Партии", "Games"),
        ["link.allgames"] = ("Все игры", "All games"),
        ["st.place"] = ("место", "place"),
        ["rules.title"] = ("Порядок начисления очков", "Scoring system"),
        ["rules.sub"] = ("Результаты партий", "Game results"),
        ["rules.win"] = ("Победа", "Win"),
        ["rules.draw"] = ("Ничья", "Draw"),
        ["rules.loss"] = ("Поражение", "Loss"),
        ["rules.streak"] = ("🔥 2 победы подряд", "🔥 2 wins in a row"),
        ["rules.bonus"] = ("Бонус ×2", "Bonus ×2"),
        ["act.loginreg"] = ("Войти и записаться", "Sign in & register"),
        ["act.login"] = ("Войти", "Sign in"),
        ["act.join"] = ("Присоединиться", "Join"),
        ["act.injoined"] = ("Вы в турнире ✓", "You're in ✓"),
        ["act.register"] = ("Записаться", "Register"),
        ["act.registered"] = ("Вы записаны ✓", "Registered ✓"),
        ["res.white"] = ("Победили белые", "White won"),
        ["res.black"] = ("Победили чёрные", "Black won"),
        ["res.draw"] = ("Ничья", "Draw"),
        ["pod.games"] = ("игр", "games"),
        ["pod.wins"] = ("побед", "wins"),
        ["pod.pts"] = ("очков", "pts"),
        ["player.default"] = ("Игрок", "Player"),
        ["chess.promote"] = ("Превратить в:", "Promote to:"),

        // --- Все игры (AllGames) ---
        ["ag.tournament"] = ("Турнир", "Tournament"),
        ["ag.title"] = ("Все партии", "All games"),
        ["ag.onboard"] = ("на доске", "on the board"),
        ["ag.empty"] = ("Сейчас нет активных партий.", "No active games right now."),
        ["ag.back"] = ("Вернуться к турниру", "Back to tournament"),
        ["ag.more"] = ("Показать ещё", "Show more"),
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

    private static readonly string[] WdRu = ["ВС", "ПН", "ВТ", "СР", "ЧТ", "ПТ", "СБ"];
    private static readonly string[] WdEn = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] MoRu = ["янв.", "февр.", "мар.", "апр.", "мая", "июн.", "июл.", "авг.", "сент.", "окт.", "нояб.", "дек."];
    private static readonly string[] MoEn = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>Метка даты в шапке расписания: «ПТ 26 июн.» / «Fri 26 Jun».</summary>
    public static string DateLabel(DateTimeOffset d) => IsEn
        ? $"{WdEn[(int)d.DayOfWeek]} {d.Day} {MoEn[d.Month - 1]}"
        : $"{WdRu[(int)d.DayOfWeek]} {d.Day} {MoRu[d.Month - 1]}";

    /// <summary>«N участников» с правильным склонением (RU) / «N participant(s)» (EN).</summary>
    public static string Participants(int n)
    {
        if (IsEn) return n == 1 ? "participant" : "participants";
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return "участник";
        if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return "участника";
        return "участников";
    }

    /// <summary>«осталось ~N мин» / «~N min left».</summary>
    public static string MinutesLeft(int min) => IsEn ? $"~{min} min left" : $"осталось ~{min} мин";

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
