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

        ["loading"] = ("Загрузка…", "Loading…"),

        // --- ЛК школы (School) ---
        ["school.pagetitle"] = ("ЛК школы — ученики", "School area — students"),
        ["school.h1"] = ("Шахматная школа №1", "Chess School #1"),
        ["school.sub"] = ("Рейтинг и статистика учеников. Нажмите на имя — откроется профиль с графиком.",
                         "Student ratings and stats. Click a name to open the profile with a chart."),
        ["school.newname"] = ("Имя нового ученика", "New student's name"),
        ["school.add"] = ("Добавить", "Add"),
        ["school.col.student"] = ("Ученик", "Student"),
        ["school.col.rating"] = ("Рейтинг", "Rating"),
        ["school.col.games"] = ("Партий", "Games"),
        ["school.col.winrate"] = ("% побед", "Win %"),
        ["school.col.account"] = ("Аккаунт", "Account"),
        ["school.col.parent"] = ("Родителю", "Parent"),
        ["school.linked"] = ("привязан", "linked"),
        ["school.link"] = ("Привязать", "Link"),
        ["school.share"] = ("Ссылка", "Link"),
        ["school.emaillabel"] = ("Email онлайн-аккаунта ученика:", "Student's online account email:"),
        ["school.cancel"] = ("Отмена", "Cancel"),
        ["school.shareparent"] = ("Ссылка для родителя (только просмотр):", "Parent link (view-only):"),
        ["school.linkerr"] = ("Пользователь с таким email не найден.", "No user found with this email."),
        ["school.birthdate"] = ("Дата рождения", "Date of birth"),
        ["school.search"] = ("Поиск по имени…", "Search by name…"),
        ["school.edit"] = ("Изменить", "Edit"),
        ["school.save"] = ("Сохранить", "Save"),
        ["school.empty"] = ("Пока нет учеников. Добавьте первого выше.", "No students yet. Add the first one above."),
        ["school.nomatch"] = ("Никого не найдено по запросу.", "No students match your search."),

        // --- Атрибуция (Attribution) ---
        ["attr.pagetitle"] = ("Очередь атрибуции партий", "Game attribution queue"),
        ["attr.h1"] = ("Атрибуция тренировочных партий", "Training game attribution"),
        ["attr.intro"] = ("Партии, записанные приложением без чек-ина, попадают сюда. Назначьте игроков и цвета — после этого партия учитывается в рейтинге.",
                         "Games recorded by the app without check-in land here. Assign players and colors — then the game counts toward the rating."),
        ["attr.empty"] = ("Очередь пуста — все партии атрибутированы. 🎉", "Queue is empty — all games attributed. 🎉"),
        ["attr.board"] = ("Доска:", "Device:"),
        ["attr.white"] = ("Белые:", "White:"),
        ["attr.black"] = ("Чёрные:", "Black:"),
        ["attr.result"] = ("Итог:", "Result:"),
        ["attr.save"] = ("Сохранить", "Save"),

        // --- Онлайн-игра (Play), включая статусы в JS ---
        ["play.pagetitle"] = ("Играть онлайн", "Play online"),
        ["play.h1"] = ("Онлайн-партия", "Online game"),
        ["play.loginprompt"] = ("Войдите единым аккаунтом ChessSchool ID, чтобы играть.", "Sign in with your ChessSchool ID to play."),
        ["play.loginbtn"] = ("Войти через ChessSchool ID", "Sign in with ChessSchool ID"),
        ["play.connecting"] = ("Подключение к игровому серверу…", "Connecting to the game server…"),
        ["play.find"] = ("Найти соперника (блиц 5+2)", "Find opponent (blitz 5+2)"),
        ["play.resign"] = ("Сдаться", "Resign"),
        ["play.finished"] = ("Партия завершена — ", "Game over — "),
        ["play.yourmove"] = ("Ваш ход", "Your move"),
        ["play.oppmove"] = ("Ход соперника", "Opponent's move"),
        ["play.whitewon"] = ("победили белые", "white won"),
        ["play.blackwon"] = ("победили чёрные", "black won"),
        ["play.draw"] = ("ничья", "draw"),
        ["play.reconnecting"] = ("Переподключение…", "Reconnecting…"),
        ["play.connected"] = ("Подключено. Нажмите «Найти соперника».", "Connected. Click \"Find opponent\"."),
        ["play.searching"] = ("Ищем соперника…", "Looking for an opponent…"),
        ["play.notfound"] = ("Соперник не найден. Попробуйте ещё раз.", "No opponent found. Try again."),
        ["play.cancelsearch"] = ("Отменить поиск", "Cancel search"),
        ["play.error"] = ("Ошибка соединения. Попробуйте ещё раз.", "Connection error. Try again."),

        // --- Профили (StudentProfile / PublicProfile / ProfileView / RatingChart) ---
        ["profile.fallback"] = ("Профиль ученика", "Student profile"),
        ["profile.notfound"] = ("Ученик не найден.", "Student not found."),
        ["profile.tolist"] = ("← К списку учеников", "← Back to students"),
        ["public.invalid"] = ("Ссылка недействительна или истекла.", "The link is invalid or has expired."),
        ["public.sub"] = ("Профиль ученика для родителя (только просмотр).", "Student profile for parents (view-only)."),
        ["pv.rating"] = ("Рейтинг", "Rating"),
        ["pv.games"] = ("Партий", "Games"),
        ["pv.wins"] = ("Побед", "Wins"),
        ["pv.winrate"] = ("% побед", "Win %"),
        ["pv.dynamics"] = ("Динамика рейтинга", "Rating trend"),
        ["pv.recent"] = ("Последние партии", "Recent games"),
        ["pv.nogames"] = ("Партий пока нет.", "No games yet."),
        ["pv.col.date"] = ("Дата", "Date"),
        ["pv.col.color"] = ("Цвет", "Color"),
        ["pv.col.opp"] = ("Соперник", "Opponent"),
        ["pv.col.result"] = ("Итог", "Result"),
        ["pv.col.delta"] = ("± рейтинг", "± rating"),
        ["pv.white"] = ("Белые", "White"),
        ["pv.black"] = ("Чёрные", "Black"),
        ["pv.draw"] = ("Ничья", "Draw"),
        ["pv.win"] = ("Победа", "Win"),
        ["pv.loss"] = ("Поражение", "Loss"),
        ["rc.aria"] = ("График рейтинга", "Rating chart"),
        ["rc.nodata"] = ("Недостаточно данных для графика.", "Not enough data for a chart."),
        ["chess.promote"] = ("Превратить в:", "Promote to:"),
    };

    public static string T(string key) =>
        Ui.TryGetValue(key, out var v) ? (IsEn ? v.En : v.Ru) : key;
}
