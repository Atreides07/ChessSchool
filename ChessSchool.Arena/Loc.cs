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
        ["nav.broadcasts"] = ("Трансляции", "Broadcasts"),
        ["nav.admin"] = ("Админка", "Admin"),
        ["search.placeholder"] = ("Поиск по турнирам", "Search tournaments"),
        ["auth.login"] = ("Войти", "Sign in"),
        ["bc.title"] = ("Трансляции", "Broadcasts"),
        ["bc.sub"] = ("Прямые трансляции топ-событий мировых шахмат. Время местное у организаторов.",
                      "Live broadcasts of top world chess events. Local time at the organizers."),
        ["bc.pagetitle"] = ("Шахматные трансляции — расписание | ChessArena",
                            "Chess broadcasts — schedule | ChessArena"),
        ["bc.more"] = ("Подробнее →", "Details →"),
        ["bc.empty"] = ("Трансляций пока нет.", "No broadcasts yet."),
        ["bc.metaprefix"] = ("Трансляции известных мировых шахматных турниров:",
                             "Broadcasts of notable world chess tournaments:"),
        ["bc.metasuffix"] = ("и другие. Даты, города, форматы.", "and more. Dates, cities, formats."),
        ["detail.dates"] = ("Даты", "Dates"),
        ["detail.place"] = ("Место", "Location"),
        ["detail.format"] = ("Формат", "Format"),
        ["detail.series"] = ("Серия", "Series"),
        ["detail.site"] = ("Сайт организатора ↗", "Organizer's site ↗"),
        ["detail.all"] = ("← Все трансляции", "← All broadcasts"),
        ["detail.notfound"] = ("Трансляция не найдена.", "Broadcast not found."),
        ["crumb.home"] = ("Главная", "Home"),
        ["crumb.broadcasts"] = ("Трансляции", "Broadcasts"),

        // --- Админка (/admin) ---
        ["admin.title"] = ("Админка", "Admin"),
        ["admin.sub"] = ("Управление контентом сайта", "Site content management"),
        ["admin.section.broadcasts"] = ("Трансляции", "Broadcasts"),
        ["admin.section.broadcasts.desc"] = ("Добавление, редактирование, скрытие трансляций",
                                             "Add, edit and hide broadcasts"),
        ["admin.section.brand"] = ("Бренд-турниры", "Brand tournaments"),
        ["admin.section.brand.desc"] = ("Кураторские турниры: «Главные» на доске и индексация",
                                        "Curated tournaments: featured on the board and indexed"),
        ["admin.bt.title"] = ("Бренд-турниры", "Brand tournaments"),
        ["admin.bt.new"] = ("Добавить бренд-турнир", "Add brand tournament"),
        ["admin.bt.edit"] = ("Редактирование бренд-турнира", "Edit brand tournament"),
        ["admin.bt.create"] = ("Новый бренд-турнир", "New brand tournament"),
        ["admin.bt.empty"] = ("Бренд-турниров пока нет.", "No brand tournaments yet."),
        ["admin.btf.desc"] = ("Описание", "Description"),
        ["admin.btf.slug.hint"] = ("Генерируется из названия, можно изменить. Часть адреса /t/…",
                                   "Generated from the name, editable. Part of the /t/… URL"),
        ["bt.readmore"] = ("Читать", "Read more"),
        ["bt.close"] = ("Закрыть", "Close"),
        ["admin.btf.start"] = ("Старт (дата и время)", "Start (date and time)"),
        ["admin.btf.initial"] = ("Контроль, минут", "Time control, minutes"),
        ["admin.btf.increment"] = ("Инкремент, сек", "Increment, sec"),
        ["admin.btf.duration"] = ("Длительность, минут", "Duration, minutes"),
        ["admin.err.duration"] = ("Длительность и контроль должны быть больше нуля.",
                                  "Duration and time control must be greater than zero."),
        ["admin.bc.title"] = ("Трансляции", "Broadcasts"),
        ["admin.bc.new"] = ("Добавить трансляцию", "Add broadcast"),
        ["admin.bc.edit"] = ("Редактирование трансляции", "Edit broadcast"),
        ["admin.bc.create"] = ("Новая трансляция", "New broadcast"),
        ["admin.bc.empty"] = ("Трансляций пока нет.", "No broadcasts yet."),
        ["admin.bc.visible"] = ("Видна", "Visible"),
        ["admin.bc.hidden"] = ("Скрыта", "Hidden"),
        ["admin.bc.show"] = ("Показать", "Show"),
        ["admin.bc.hide"] = ("Скрыть", "Hide"),
        ["admin.bc.edit.btn"] = ("Изменить", "Edit"),
        ["admin.bc.delete"] = ("Удалить", "Delete"),
        ["admin.bc.delete.confirm"] = ("Удалить трансляцию безвозвратно?", "Delete this broadcast permanently?"),
        ["admin.f.slug"] = ("Идентификатор (URL)", "Slug (URL)"),
        ["admin.f.slug.hint"] = ("Латиница, цифры и дефисы. Часть адреса /broadcasts/…", "Latin letters, digits, dashes. Part of /broadcasts/… URL"),
        ["admin.f.name"] = ("Название", "Name"),
        ["admin.f.series"] = ("Серия", "Series"),
        ["admin.f.seriescls"] = ("Стиль чипа серии", "Series chip style"),
        ["admin.f.start"] = ("Дата начала", "Start date"),
        ["admin.f.end"] = ("Дата окончания", "End date"),
        ["admin.f.city"] = ("Город", "City"),
        ["admin.f.country"] = ("Страна", "Country"),
        ["admin.f.flag"] = ("Флаг (эмодзи)", "Flag (emoji)"),
        ["admin.f.format"] = ("Формат", "Format"),
        ["admin.f.url"] = ("Ссылка на сайт организатора", "Organizer's site URL"),
        ["admin.f.image"] = ("URL фонового изображения", "Background image URL"),
        ["admin.f.image.hint"] = ("Вставьте ссылку с официального источника ИЛИ загрузите файл в наше хранилище (надёжнее — не зависит от внешнего сайта).",
                                  "Paste a link from an official source OR upload a file to our storage (more reliable — independent of external sites)."),
        ["admin.f.image.upload"] = ("Загрузить файл", "Upload file"),
        ["admin.f.image.uploading"] = ("Загрузка…", "Uploading…"),
        ["admin.f.image.upload.hint"] = ("Файл сохранится в наше хранилище (S3): источник нельзя подменить.",
                                         "The file is stored in our storage (S3): the source can't be swapped."),
        ["admin.err.image.type"] = ("Поддерживаются JPEG, PNG, WEBP, AVIF, GIF.", "Supported: JPEG, PNG, WEBP, AVIF, GIF."),
        ["admin.err.image.size"] = ("Файл больше 5 МБ.", "File is larger than 5 MB."),
        ["admin.err.image.failed"] = ("Не удалось загрузить изображение.", "Failed to upload the image."),
        ["admin.f.visible"] = ("Показывать на сайте", "Show on the site"),
        ["admin.save"] = ("Сохранить", "Save"),
        ["admin.cancel"] = ("Отмена", "Cancel"),
        ["admin.back"] = ("← К списку", "← Back to list"),
        ["admin.preview"] = ("Превью", "Preview"),
        ["admin.err.slug"] = ("Укажите корректный идентификатор (латиница, цифры, дефисы).",
                              "Enter a valid slug (Latin letters, digits, dashes)."),
        ["admin.err.name"] = ("Укажите название.", "Enter a name."),
        ["admin.err.dates"] = ("Дата окончания не может быть раньше даты начала.",
                               "End date cannot be earlier than the start date."),
        ["admin.err.slug.exists"] = ("Трансляция с таким идентификатором уже есть.", "A broadcast with this slug already exists."),

        // --- Расписание (Home) ---
        ["loading"] = ("Загрузка…", "Loading…"),
        ["bt.featured"] = ("Главные турниры", "Featured tournaments"),
        ["bt.watch"] = ("Смотреть", "Watch"),
        ["bt.details"] = ("Подробнее", "Details"),
        ["bt.remind"] = ("Напомнить", "Remind me"),
        ["bt.remind.hint"] = ("Добавить в календарь", "Add to calendar"),
        ["bt.results"] = ("Результаты", "Results"),
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
