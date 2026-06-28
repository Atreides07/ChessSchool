using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.Arena.Grains;

// ----------------------- Интерфейсы грейнов -----------------------

/// <summary>Каталог турниров (синглтон, ключ 0). Генерирует расписание и отдаёт список.</summary>
public interface IArenaDirectoryGrain : IGrainWithIntegerKey
{
    Task<IReadOnlyList<TournamentSummaryDto>> ListAsync(string? sub = null);
}

/// <summary>
/// Грейн одного арена-турнира (ключ = уникальный id). Жизненный цикл по времени:
/// Created (регистрация) → Running (непрерывный пейринг, игра) → Finished (результаты).
/// Боты добираются до минимума участников и сокращаются по мере прихода людей (как на lichess).
/// </summary>
public interface IArenaTournamentGrain : IGrainWithStringKey
{
    Task ConfigureAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds);
    Task ConfigureFinishedDemoAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds);
    /// <summary>Конфигурация бренд-турнира из каталога админки (можно переконфигурировать до старта).</summary>
    Task ConfigureBrandAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds);
    Task JoinAsync(string sub, string name);
    /// <summary>Игрок нажал «подобрать соперника» — войти в пул подбора (подбор не автоматический).</summary>
    Task SeekAsync(string sub);
    Task<ArenaStateDto> GetStateAsync(string sub);
    Task<TournamentSummaryDto> GetSummaryAsync(string? sub = null);
    Task<IReadOnlyList<ArenaBoardDto>> GetBoardsAsync();
    Task<ArenaGameDto?> MoveAsync(string sub, MoveInput move);
    Task BerserkAsync(string sub);
    Task ResignAsync(string sub);
}

// ----------------------- Каталог / расписание -----------------------

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
            grains.GetGrain<IArenaTournamentGrain>(id).GetSummaryAsync(sub)));

        var list = future.Concat(live).OrderBy(t => t.StartsAt).ToList();

        _cache[key] = (now + CacheTtl, list);
        if (_cache.Count > 64) // чистим протухшие, чтобы кэш не рос по числу разных sub
            foreach (var k in _cache.Where(kv => kv.Value.Exp <= now).Select(kv => kv.Key).ToList())
                _cache.Remove(k);
        return list;
    }
}

// ----------------------- Персистентное состояние турнира -----------------------

/// <summary>
/// Долговечная часть турнира (мета + таблица). Сохраняется в grain storage, поэтому переживает
/// деактивацию грейна: при повторной активации очки/серии/история партий восстанавливаются.
/// Активные партии (доски) намеренно НЕ сохраняем — при реактивации простаивающие игроки
/// мгновенно переспариваются, прерванная партия начинается заново (потеря одной партии терпима).
/// </summary>
[GenerateSerializer]
public sealed class ArenaPersistedState
{
    [Id(0)] public bool Configured { get; set; }
    [Id(1)] public bool FinishedDemo { get; set; }
    [Id(2)] public string Name { get; set; } = "";
    [Id(3)] public TimeControl Tc { get; set; } = new(180, 0);
    [Id(4)] public int DurationSeconds { get; set; }
    [Id(5)] public DateTimeOffset StartsAt { get; set; }
    [Id(6)] public int BotCounter { get; set; }
    [Id(7)] public List<PersistedPlayer> Players { get; set; } = [];
}

[GenerateSerializer]
public sealed class PersistedPlayer
{
    [Id(0)] public string Key { get; set; } = "";
    [Id(1)] public string Name { get; set; } = "";
    [Id(2)] public bool IsBot { get; set; }
    [Id(3)] public int Score { get; set; }
    [Id(4)] public int Streak { get; set; }
    [Id(5)] public int Games { get; set; }
    [Id(6)] public int Wins { get; set; }
    [Id(7)] public List<int> Results { get; set; } = [];
}

// ----------------------- Турнир -----------------------

public sealed class ArenaTournamentGrain(
    [PersistentState("tournament", "arena")] IPersistentState<ArenaPersistedState> store,
    ArenaNotifier notifier,
    IChessEngine engine,
    ArenaRuntimeOptions runtime,
    IAnalytics analytics,
    IServiceProvider services,
    ILogger<ArenaTournamentGrain> logger) : Grain, IArenaTournamentGrain, IRemindable
{
    private sealed class Player
    {
        public string Name = "";
        public int Score;
        public int Streak;
        public bool Playing;
        public string? GameId;
        public bool IsBot;
        // Сила/скорость бота (для людей не используются). Назначаются из BotPersona по ключу бота.
        public int Rating;
        public int Skill;            // уровень Stockfish 0..20
        public double SpeedFactor = 1.0; // множитель времени на обдумывание (слабые ходят быстрее)
        // Игрок нажал «подобрать соперника» и ждёт пары. Подбор НЕ автоматический: до нажатия (и после
        // каждой партии) Seeking=false — игрок просто записан, в пул подбора не входит. Боты всегда «ищут».
        public bool Seeking;
        public DateTimeOffset? WaitingSince;
        public int Games;
        public int Wins;
        public readonly List<int> Results = new(); // очки за каждую сыгранную партию (0/1/2/4)
        public bool OnFire => Streak >= 2;
    }

    // Желаемое число ботов в идущем турнире — настраивается в админке по типу игры (BotSettingsGrain).
    // Кэшируется на грейне и обновляется с троттлингом (не дёргаем конфиг-грейн на каждый тик).
    private int? _botTarget;
    private DateTimeOffset _botSettingsAt;
    private int BotTarget => _botTarget ?? BotSettingsGrain.DefaultCount;
    // Сколько секунд человек ждёт соперника-человека, прежде чем к нему подключат бота.
    private const int WaitForBotSeconds = 10;
    // Хвост показа завершённой партии БЕЗ живого участника (бот-vs-бот / человек уже ушёл): зрители
    // успевают увидеть финал, потом партия убирается. Партию, которую смотрит человек, держим без таймера.
    private const int FinishedLingerSeconds = 6;
    private int _botCounter;

    private static readonly string[] BotNames =
        ["Stockfish_15", "DeepBlue_v2", "AlphaZero", "Komodo_X", "Leela_Z", "Fritz_9", "Houdini", "Rybka"];

    private sealed class Game
    {
        public string Id = "";
        public string WhiteSub = "", WhiteName = "", BlackSub = "", BlackName = "";
        public ChessGame Board = new();
        public long WhiteMs, BlackMs;
        public bool WhiteMoved, BlackMoved;
        public bool WhiteBerserk, BlackBerserk;
        public DateTimeOffset LastMoveAt;
        public DateTimeOffset StartedAt;
        public GameStatus Status = GameStatus.InProgress;
        public GameResult Result;
        public GameEndReason Reason;
        public DateTimeOffset? FinishedAt;
        public DateTimeOffset? BotThinkUntil; // до этого момента бот «думает» над ходом (неравномерно)
        public int BotPlannedMs;              // запланированное время обдумывания текущего хода
    }

    private bool _configured;
    private bool _finishedDemo;
    private bool _dirty;
    private string _name = "";
    private TimeControl _tc = TimeControl.Blitz;
    private int _durationSeconds;
    private DateTimeOffset _startsAt;
    private int _gameCounter;
    private IDisposable? _timer;
    private bool _reminderRegistered;
    private const string TickReminder = "arena-tick";

    private readonly Dictionary<string, Player> _players = new();
    private readonly Dictionary<string, Game> _games = new();

    private string Id => this.GetPrimaryKeyString();

    // ----------------------- Активация / персистентность -----------------------

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (store.State.Configured) LoadFromStore();
        return base.OnActivateAsync(cancellationToken);
    }

    /// <summary>Восстанавливает мету и таблицу из хранилища (после деактивации/реактивации грейна).</summary>
    private void LoadFromStore()
    {
        var s = store.State;
        _configured = true;
        _finishedDemo = s.FinishedDemo;
        _name = s.Name;
        _tc = s.Tc;
        _durationSeconds = s.DurationSeconds;
        _startsAt = s.StartsAt;
        _botCounter = s.BotCounter;
        foreach (var p in s.Players)
        {
            var pl = new Player { Name = p.Name, IsBot = p.IsBot, Score = p.Score, Streak = p.Streak, Games = p.Games, Wins = p.Wins };
            pl.Results.AddRange(p.Results);
            if (pl.IsBot) // сила/скорость бота не хранятся — восстанавливаем детерминированно из ключа
            {
                var persona = BotPersona.For(p.Key);
                pl.Rating = persona.Rating; pl.Skill = persona.Skill; pl.SpeedFactor = persona.Speed;
            }
            _players[p.Key] = pl; // runtime-поля (Playing/GameId/WaitingSince) сбрасываются — игрок переспарится
        }
        EnsureTimer();
    }

    /// <summary>Грейн сам выводит мету из своего id (см. <see cref="ArenaSchedule"/>) при первом обращении.</summary>
    private void EnsureConfigured()
    {
        if (_configured) return;
        if (ArenaSchedule.Resolve(Id) is not { } meta) return; // id вне расписания (напр. тестовый) — ждём ConfigureAsync

        _configured = true;
        _name = meta.Name;
        _tc = meta.Tc;
        _startsAt = meta.StartsAt.ToUniversalTime();
        _durationSeconds = meta.DurationSeconds;

        // Слот, который уже закончился к моменту первого появления на сервере, — без реальной истории:
        // показываем детерминированную симуляцию. Турнир, прошедший вживую, сохраняет настоящую таблицу.
        if (_startsAt.AddSeconds(_durationSeconds) <= DateTimeOffset.UtcNow)
        {
            SimulateFinished();
            _finishedDemo = true;
        }
        _dirty = true;
        EnsureTimer();
    }

    private void Snapshot()
    {
        var s = store.State;
        s.Configured = _configured;
        s.FinishedDemo = _finishedDemo;
        s.Name = _name;
        s.Tc = _tc;
        s.DurationSeconds = _durationSeconds;
        s.StartsAt = _startsAt;
        s.BotCounter = _botCounter;
        s.Players = _players.Select(kv => new PersistedPlayer
        {
            Key = kv.Key,
            Name = kv.Value.Name,
            IsBot = kv.Value.IsBot,
            Score = kv.Value.Score,
            Streak = kv.Value.Streak,
            Games = kv.Value.Games,
            Wins = kv.Value.Wins,
            Results = kv.Value.Results.ToList()
        }).ToList();
    }

    private async Task FlushAsync()
    {
        if (!_dirty) return;
        Snapshot();
        await store.WriteStateAsync();
        _dirty = false;
    }

    private TournamentStatus Status()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _startsAt) return TournamentStatus.Created;
        if (now < _startsAt.AddSeconds(_durationSeconds)) return TournamentStatus.Running;
        return TournamentStatus.Finished;
    }

    public async Task ConfigureAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds)
    {
        if (_configured) return;
        _configured = true;
        _name = name;
        _tc = tc;
        _startsAt = startsAt.ToUniversalTime();
        _durationSeconds = durationSeconds;
        _dirty = true;
        EnsureTimer();
        await FlushAsync();
    }

    // Состав завершённых турниров: имя + «сила» (влияет на вероятность победы) + признак бота.
    private static readonly (string Name, double Strength, bool Bot)[] FinishedRoster =
    [
        ("ArenaHost_0", 1.35, false), ("Zugzwang_42", 1.30, false), ("Leela_Zero", 1.20, true),
        ("French_Winawer", 1.10, false), ("DeepBlue_v2", 1.05, true), ("Morphy_Machine", 1.00, false),
        ("Stockfish_15", 0.95, true), ("Komodo_X", 0.90, true), ("Tal_Tactics", 0.85, false),
        ("Rook_Rampage", 0.80, false), ("Endgame_Esra", 0.75, false), ("Fritz_9", 0.70, true),
    ];

    /// <summary>
    /// «Проигрывает» завершённый турнир детерминированно (сид от id): пейринг по очкам, исходы
    /// взвешены силой игроков, начисление — строго по <see cref="ArenaScoring"/>. Таблица и история
    /// партий получаются реальными (а не случайными числами) и согласованы с «Порядком начисления очков».
    /// </summary>
    public async Task ConfigureFinishedDemoAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds)
    {
        if (_configured) return;
        _configured = true;
        _finishedDemo = true;
        _name = name;
        _tc = tc;
        _startsAt = startsAt.ToUniversalTime();
        _durationSeconds = durationSeconds;
        SimulateFinished();
        _dirty = true;
        await FlushAsync();
    }

    /// <summary>
    /// Конфигурация бренд-турнира из каталога админки. Первый вызов — полная настройка; правки до старта
    /// (Created) обновляют расписание; идущий турнир обновляет только имя; завершённый не трогаем.
    /// Грейн персистится → переживает деактивацию (на другой ноде восстановится из storage).
    /// </summary>
    public async Task ConfigureBrandAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds)
    {
        if (_configured && Status() == TournamentStatus.Finished) return;
        bool beforeStart = !_configured || Status() == TournamentStatus.Created;
        _name = name;
        if (beforeStart)
        {
            _tc = tc;
            _startsAt = startsAt.ToUniversalTime();
            _durationSeconds = durationSeconds;
        }
        _configured = true;
        _dirty = true;
        EnsureTimer();
        await FlushAsync();
    }

    private void SimulateFinished()
    {
        // Детерминированный сид от id турнира → одинаковая история при каждом просмотре.
        int seed = 17;
        foreach (var ch in Id) seed = unchecked(seed * 31 + ch) & 0x7fffffff;
        var rng = new Random(seed);

        int count = 8 + rng.Next(0, 5); // 8..12 участников
        var roster = FinishedRoster.OrderBy(_ => rng.Next()).Take(count).ToList();
        var strength = new Dictionary<string, double>();
        foreach (var (rname, str, bot) in roster)
        {
            _players[rname] = new Player { Name = rname, IsBot = bot };
            strength[rname] = str * (0.85 + rng.NextDouble() * 0.3); // лёгкий разброс формы
        }

        // Число туров оцениваем по длительности и средней партии данного контроля.
        int avgGameSec = Math.Max(45, _tc.InitialSeconds + _tc.IncrementSeconds * 20);
        int rounds = Math.Clamp(_durationSeconds / Math.Max(30, avgGameSec / 4), 8, 22);

        for (int r = 0; r < rounds; r++)
        {
            // Пейринг по очкам (как на lichess), внутри равных очков — случайно.
            var order = _players.Values
                .OrderByDescending(p => p.Score).ThenBy(_ => rng.Next())
                .ToList();
            for (int i = 0; i + 1 < order.Count; i += 2)
            {
                var a = order[i];
                var b = order[i + 1];
                double pa = strength[a.Name], pb = strength[b.Name];
                if (rng.NextDouble() < 0.18) { Award(a, 0.5); Award(b, 0.5); } // ничья
                else if (rng.NextDouble() * (pa + pb) < pa) { Award(a, 1.0); Award(b, 0.0); }
                else { Award(a, 0.0); Award(b, 1.0); }
            }
        }
    }

    private void EnsureTimer()
    {
        if (Status() != TournamentStatus.Running) return;
        // Пока турнир идёт, держим грейн активным: иначе при простое он деактивируется,
        // партии встанут, а боты перестанут ходить. Состояние всё равно персистится.
        DelayDeactivation(TimeSpan.FromMinutes(10));
        // 500 мс: достаточно мелкий шаг, чтобы тайминг ходов ботов был неравномерным, а не «по метроному».
        _timer ??= this.RegisterGrainTimer(OnTimerAsync, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

        // Reminder (есть Redis) воскрешает грейн на ЛЮБОЙ ноде даже при внезапной потере текущей ноды
        // (таймер живёт только в активном грейне). Гранулярность reminder'а — 1 мин (минимум Orleans):
        // он лишь возвращает грейн к жизни, после чего мелкий таймер снова ведёт турнир посекундно.
        if (runtime.RemindersEnabled && !_reminderRegistered)
        {
            _reminderRegistered = true;
            _ = RegisterTickReminderSafe();
        }
    }

    private async Task RegisterTickReminderSafe()
    {
        try
        {
            await this.RegisterOrUpdateReminder(TickReminder, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось зарегистрировать reminder тика турнира {Id}.", Id);
            _reminderRegistered = false;
        }
    }

    private async Task UnregisterTickReminderSafe()
    {
        try
        {
            if (await this.GetReminder(TickReminder) is { } r) await this.UnregisterReminder(r);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Снятие reminder турнира {Id}.", Id);
        }
        _reminderRegistered = false;
    }

    /// <summary>Reminder-«воскрешение»: вернувшись к жизни, грейн возобновляет тик (или снимает reminder, если турнир уже завершён).</summary>
    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != TickReminder) return;
        EnsureConfigured();
        if (Status() == TournamentStatus.Running)
        {
            EnsureTimer();
            await OnTimerAsync();
        }
        else if (Status() == TournamentStatus.Finished)
        {
            await UnregisterTickReminderSafe();
        }
    }

    private async Task OnTimerAsync()
    {
        await RefreshBotSettingsAsync();
        Tick();
        await DriveBotsAsync(); // ходы ботов через Stockfish (серверная игра)
        await FlushAsync();
        notifier.Notify(Id);
    }

    /// <summary>
    /// Подтягивает желаемое число ботов для типа этого турнира из настроек (BotSettingsGrain), с
    /// троттлингом (не чаще раза в 15с) — чтобы правки из админки применялись, но без вызова конфиг-грейна
    /// на каждый тик. id вне расписания (напр. тестовый/бренд) → дефолт.
    /// </summary>
    private async Task RefreshBotSettingsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (_botTarget is not null && (now - _botSettingsAt).TotalSeconds < 15) return;
        _botSettingsAt = now;
        var type = ArenaSchedule.TypeOf(Id);
        if (type is null) { _botTarget = BotSettingsGrain.DefaultCount; return; }
        try { _botTarget = await GrainFactory.GetGrain<IBotSettingsGrain>(0).GetCountAsync(type); }
        catch { _botTarget ??= BotSettingsGrain.DefaultCount; } // конфиг недоступен — дефолт, не падаем
    }

    public async Task JoinAsync(string sub, string name)
    {
        EnsureConfigured();
        await RefreshBotSettingsAsync();
        // Регистрация возможна и до старта (Created), и во время турнира (Running).
        if (Status() == TournamentStatus.Finished) return;
        if (!_players.ContainsKey(sub))
        {
            // Только запись в турнир. В пул подбора игрок войдёт сам по кнопке «подобрать соперника»
            // (SeekAsync) — соперник (в т.ч. бот) автоматически не назначается.
            _players[sub] = new Player { Name = name };
            _dirty = true;
            analytics.Capture("tournament_joined", sub, new Dictionary<string, object?>
            {
                ["tournament_id"] = Id,
                ["time_control"] = _tc.ToString(),
            });
        }
        EnsureTimer();
        Tick();
        await FlushAsync();
        notifier.Notify(Id);
    }

    /// <summary>
    /// Игрок нажал «подобрать соперника»: входит в пул подбора. Подбор не автоматический — пока флаг не
    /// взведён, игрок просто записан. Сразу пробуем спарить (если есть другой ищущий человек); иначе он
    /// ждёт, и через <see cref="WaitForBotSeconds"/> к нему подключится бот (как и раньше после нажатия).
    /// </summary>
    public async Task SeekAsync(string sub)
    {
        EnsureConfigured();
        await RefreshBotSettingsAsync();
        if (Status() != TournamentStatus.Running) return; // искать соперника можно только в идущем турнире
        if (_players.TryGetValue(sub, out var p) && !p.IsBot)
        {
            // Если игрок смотрел результат завершённой партии — отцепляем его (доска уезжает, а сама
            // партия без живого участника позже убирается в Tick). От ИДУЩЕЙ партии «искать» нельзя.
            bool inActiveGame = p.GameId is { } gid && _games.TryGetValue(gid, out var g)
                && g.Status == GameStatus.InProgress;
            if (!inActiveGame)
            {
                p.Playing = false;
                p.GameId = null;
                if (!p.Seeking) { p.Seeking = true; p.WaitingSince = DateTimeOffset.UtcNow; }
            }
        }
        EnsureTimer();
        Tick();          // попытка мгновенного пейринга с другим ищущим
        await FlushAsync();
        notifier.Notify(Id);
    }

    public async Task<TournamentSummaryDto> GetSummaryAsync(string? sub = null)
    {
        EnsureConfigured();
        await RefreshBotSettingsAsync();
        Tick();
        EnsureTimer();
        await FlushAsync();
        return new TournamentSummaryDto(
            Id, _name, _tc, Status(), _players.Count, SecondsLeft(),
            _players.Values.Count(p => p.IsBot), _startsAt, _durationSeconds,
            Joined: sub is not null && _players.ContainsKey(sub));
    }

    public async Task<ArenaStateDto> GetStateAsync(string sub)
    {
        EnsureConfigured();
        await RefreshBotSettingsAsync();
        Tick();
        EnsureTimer();
        await FlushAsync();

        var standings = _players
            .OrderByDescending(p => p.Value.Score)
            .ThenByDescending(p => p.Value.Streak)
            .Select((p, i) => new ArenaStandingRow(i + 1, p.Value.Name, p.Value.Score, p.Value.Streak,
                p.Value.OnFire, p.Value.Playing, p.Value.Games, p.Value.Wins, p.Value.Results.ToList(), p.Value.IsBot))
            .ToList();

        _players.TryGetValue(sub, out var me);
        ArenaGameDto? myGame = null;
        if (me?.GameId is { } gid && _games.TryGetValue(gid, out var g))
            myGame = BuildGameDto(g, sub);

        return new ArenaStateDto(
            Id, _name, Status(), SecondsLeft(), me is not null,
            me?.Score ?? 0, standings, myGame,
            _tc, _startsAt, _durationSeconds, _players.Values.Count(p => p.IsBot),
            BuildBoards(4), // в шапке турнира — только 4 доски, остальное на /games
            Seeking: me is { Seeking: true, Playing: false }); // ждёт пары (кнопка нажата, партии ещё нет)
    }

    public async Task<IReadOnlyList<ArenaBoardDto>> GetBoardsAsync()
    {
        EnsureConfigured();
        await RefreshBotSettingsAsync();
        Tick();
        EnsureTimer();
        await FlushAsync();
        return BuildBoards(int.MaxValue); // все доски — для страницы «Все игры»
    }

    /// <summary>Трансляция «идёт сейчас»: активные + только что завершённые партии (с финальным счётом).</summary>
    private IReadOnlyList<ArenaBoardDto> BuildBoards(int take)
    {
        // Завершённые партии показываем, пока они есть в _games (их срок жизни решает Tick: пока смотрит
        // человек — без таймера; иначе короткий хвост для зрителей). Здесь по таймеру не отсекаем.
        return _games.Values
            .Where(g => g.Status == GameStatus.InProgress || g.FinishedAt is not null)
            .OrderByDescending(g => g.Status == GameStatus.InProgress)
            .ThenByDescending(g => ScoreOf(g.WhiteSub) + ScoreOf(g.BlackSub))
            .Take(take)
            .Select(g => new ArenaBoardDto(
                g.Id, g.Board.Fen, g.WhiteName, g.BlackName,
                ScoreOf(g.WhiteSub), ScoreOf(g.BlackSub),
                g.WhiteMs, g.BlackMs, g.Board.Turn, g.Status, g.Result,
                g.Board.LastFrom, g.Board.LastTo, g.Board.CheckSquare,
                IsBotSub(g.WhiteSub), IsBotSub(g.BlackSub)))
            .ToList();
    }

    private int ScoreOf(string sub) => _players.TryGetValue(sub, out var p) ? p.Score : 0;

    private bool IsBotSub(string sub) => _players.TryGetValue(sub, out var p) && p.IsBot;

    public async Task<ArenaGameDto?> MoveAsync(string sub, MoveInput move)
    {
        if (!_players.TryGetValue(sub, out var player) || player.GameId is null)
            return null;
        if (!_games.TryGetValue(player.GameId, out var game) || game.Status != GameStatus.InProgress)
            return null;

        var mover = sub == game.WhiteSub ? Color.White : Color.Black;
        if (mover != game.Board.Turn) return null;

        if (DeductClock(game, mover)) { FinishGame(game); await FlushAsync(); notifier.Notify(Id); return BuildGameDto(game, sub); }

        if (!game.Board.TryMove(move.From, move.To, move.Promotion))
            return BuildGameDto(game, sub);

        if (mover == Color.White) { game.WhiteMoved = true; if (!game.WhiteBerserk) game.WhiteMs += _tc.IncrementSeconds * 1000L; }
        else { game.BlackMoved = true; if (!game.BlackBerserk) game.BlackMs += _tc.IncrementSeconds * 1000L; }
        game.LastMoveAt = DateTimeOffset.UtcNow;

        if (game.Board.IsEndGame)
        {
            (game.Result, game.Reason) = game.Board.Resolve();
            FinishGame(game);
        }

        await FlushAsync();
        notifier.Notify(Id);
        return BuildGameDto(game, sub);
    }

    public Task BerserkAsync(string sub)
    {
        if (_players.TryGetValue(sub, out var player) && player.GameId is { } gid
            && _games.TryGetValue(gid, out var g) && g.Status == GameStatus.InProgress)
        {
            if (sub == g.WhiteSub && !g.WhiteMoved && !g.WhiteBerserk) { g.WhiteMs /= 2; g.WhiteBerserk = true; }
            else if (sub == g.BlackSub && !g.BlackMoved && !g.BlackBerserk) { g.BlackMs /= 2; g.BlackBerserk = true; }
            notifier.Notify(Id);
        }
        return Task.CompletedTask;
    }

    public async Task ResignAsync(string sub)
    {
        if (_players.TryGetValue(sub, out var player) && player.GameId is { } gid
            && _games.TryGetValue(gid, out var game) && game.Status == GameStatus.InProgress)
        {
            game.Result = sub == game.WhiteSub ? GameResult.BlackWins : GameResult.WhiteWins;
            game.Reason = GameEndReason.Resignation;
            FinishGame(game);
            await FlushAsync();
            notifier.Notify(Id);
        }
    }

    // ----------------------- Внутренняя логика -----------------------

    private int SecondsLeft() => Status() == TournamentStatus.Running
        ? Math.Max(0, (int)(_startsAt.AddSeconds(_durationSeconds) - DateTimeOffset.UtcNow).TotalSeconds)
        : 0;

    private void Tick()
    {
        var status = Status();
        if (status == TournamentStatus.Finished)
        {
            _timer?.Dispose();
            _timer = null;
            if (_reminderRegistered) _ = UnregisterTickReminderSafe();
            return;
        }
        if (status != TournamentStatus.Running) return; // Created — только регистрация

        // Часы и таймауты для всех идущих партий.
        foreach (var g in _games.Values.Where(g => g.Status == GameStatus.InProgress).ToList())
            if (DeductClock(g, g.Board.Turn)) FinishGame(g);

        var now = DateTimeOffset.UtcNow;
        foreach (var g in _games.Values.Where(g => g.Status != GameStatus.InProgress).ToList())
        {
            // Завершённую партию держим, пока её смотрит участник-человек: его доска не исчезает сама,
            // только когда он нажмёт «подобрать соперника» (тогда он отцепится в SeekAsync). Партиям без
            // живого участника (бот-vs-бот или человек уже ушёл) даём короткий хвост для зрителей, затем
            // убираем — иначе _games рос бы без предела (горячий путь, неограниченная коллекция).
            bool humanHolds = IsHumanHolding(g.WhiteSub, g.Id) || IsHumanHolding(g.BlackSub, g.Id);
            if (humanHolds) continue;
            if (g.FinishedAt is { } f && (now - f).TotalSeconds <= FinishedLingerSeconds) continue;
            foreach (var s in new[] { g.WhiteSub, g.BlackSub })
                if (_players.TryGetValue(s, out var pl) && pl.GameId == g.Id) FreePlayer(s);
            _games.Remove(g.Id);
        }

        ManageBots();
        PairIdlePlayers();
    }

    /// <summary>
    /// Лichess-подобное управление ботами: добор до минимума участников, если людей мало;
    /// сокращение простаивающих ботов (вплоть до нуля), когда людей достаточно.
    /// </summary>
    private void ManageBots()
    {
        int bots = _players.Values.Count(p => p.IsBot);
        int targetBots = BotTarget; // настраивается в админке по типу игры (0 — без ботов)

        while (bots < targetBots)
        {
            SpawnBot();
            bots++;
        }

        // Лишних (свыше таргета) ботов убираем по мере их простоя — играющих не трогаем.
        if (bots > targetBots)
        {
            foreach (var kv in _players.Where(kv => kv.Value.IsBot && !kv.Value.Playing).ToList())
            {
                if (bots <= targetBots) break;
                _players.Remove(kv.Key);
                bots--;
                _dirty = true;
            }
        }
    }


    private async Task DriveBotsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var g in _games.Values.Where(g => g.Status == GameStatus.InProgress).ToList())
        {
            var botColor = g.Board.Turn;
            var moverSub = botColor == Color.White ? g.WhiteSub : g.BlackSub;
            if (!_players.TryGetValue(moverSub, out var mp) || !mp.IsBot)
            {
                g.BotThinkUntil = null; // ход не за ботом — сбрасываем таймер обдумывания
                continue;
            }

            // Неравномерный тайминг: бот «думает» дольше над сложным выбором и быстро ходит,
            // когда вариант один/вынужденный. Часы при этом тикают, как у человека.
            if (g.BotThinkUntil is null)
            {
                g.BotPlannedMs = BotThinkMs(g, mp);
                g.BotThinkUntil = now.AddMilliseconds(g.BotPlannedMs);
                continue;
            }
            if (now < g.BotThinkUntil.Value) continue; // ещё думает

            // Движку даём время, пропорциональное запланированному (но без блокировки грейна надолго);
            // сила хода — по уровню Stockfish этого бота (разные рейтинги играют по-разному).
            int engineMs = Math.Clamp(g.BotPlannedMs, 100, 450);
            var uci = await engine.GetBestMoveAsync(g.Board.Fen, mp.Skill, engineMs);
            bool moved = uci is not null && ApplyUci(g, uci);
            if (!moved) moved = g.Board.TryMakeRandomMove();
            g.BotThinkUntil = null; // следующий ход — обдумываем заново
            if (!moved) continue;

            if (botColor == Color.White) { g.WhiteMoved = true; g.WhiteMs += _tc.IncrementSeconds * 1000L; }
            else { g.BlackMoved = true; g.BlackMs += _tc.IncrementSeconds * 1000L; }
            g.LastMoveAt = DateTimeOffset.UtcNow;

            if (g.Board.IsEndGame)
            {
                (g.Result, g.Reason) = g.Board.Resolve();
                FinishGame(g);
            }
        }
    }

    /// <summary>
    /// Время обдумывания хода бота, зависящее от сложности позиции: единственный/вынужденный ход —
    /// мгновенно, много вариантов — дольше. Плюс «человеческий» джиттер, чтобы ходы не были ритмичными.
    /// </summary>
    private static int BotThinkMs(Game g, Player bot)
    {
        int options = g.Board.LegalMoveCount;
        if (options <= 1) return Random.Shared.Next(80, 200); // единственный/вынужденный ход — почти мгновенно

        // Бюджет ~ оставшееся время на ~25 предстоящих ходов: под нехватку времени бот ускоряется.
        long myMs = g.Board.Turn == Color.White ? g.WhiteMs : g.BlackMs;
        double budget = Math.Max(150, myMs / 25.0);
        // Сложность: шах и обилие вариантов → дольше; мало вариантов → быстро (доля бюджета).
        double complexity = g.Board.InCheck ? 0.9 : Math.Min(1.0, 0.25 + options * 0.02);
        // Личность: слабые ходят быстрее, сильные обстоятельнее; плюс джиттер, чтобы не «по метроному».
        double t = budget * complexity * bot.SpeedFactor * (0.7 + Random.Shared.NextDouble() * 0.6);
        return (int)Math.Clamp(t, 90, 2500);
    }

    private static bool ApplyUci(Game g, string uci)
    {
        if (uci.Length < 4) return false;
        var from = uci[..2];
        var to = uci[2..4];
        var promo = uci.Length > 4 ? uci[4].ToString() : null;
        return g.Board.TryMove(from, to, promo);
    }

    // Завершённую партию «держит» участник-человек, пока не ушёл из неё (не нажал «подобрать соперника»).
    private bool IsHumanHolding(string sub, string gameId) =>
        _players.TryGetValue(sub, out var p) && !p.IsBot && p.GameId == gameId;

    private void FreePlayer(string sub)
    {
        if (_players.TryGetValue(sub, out var p))
        {
            p.Playing = false;
            p.GameId = null;
            // После партии НЕ ищем следующего соперника автоматически: человек снова видит кнопку
            // «подобрать соперника». Боты сразу снова в пуле (играют между собой / ждут людей).
            p.Seeking = p.IsBot;
            p.WaitingSince = p.IsBot ? DateTimeOffset.UtcNow : null;
        }
    }

    private void PairIdlePlayers()
    {
        var now = DateTimeOffset.UtcNow;

        // В пул подбора входят только «ищущие» люди (нажавшие «подобрать соперника»). Записанные, но
        // не нажавшие — не спариваются (ни с человеком, ни с ботом).
        var idleHumans = _players.Where(kv => !kv.Value.IsBot && !kv.Value.Playing && kv.Value.Seeking)
            .OrderByDescending(kv => kv.Value.Score).Select(kv => kv.Key).ToList();
        var idleBots = _players.Where(kv => kv.Value.IsBot && !kv.Value.Playing)
            .Select(kv => kv.Key).ToList();

        // 1) Человек с человеком — мгновенно (приоритет живым соперникам).
        int hi = 0;
        while (hi + 1 < idleHumans.Count) { CreateGame(idleHumans[hi], idleHumans[hi + 1]); hi += 2; }

        // 2) Оставшийся человек ждёт соперника-человека; если за WaitForBotSeconds не нашёлся —
        //    подключаем бота (берём свободного либо создаём нового) и сразу спариваем.
        int bi = 0;
        for (; hi < idleHumans.Count; hi++)
        {
            var human = _players[idleHumans[hi]];
            if (human.WaitingSince is not { } since || (now - since).TotalSeconds < WaitForBotSeconds)
                continue; // ещё ищем — оставляем «Ищем соперника…»
            // Боты для этого типа отключены (таргет 0) — соперника-бота не подключаем, ждём человека.
            var bot = bi < idleBots.Count ? idleBots[bi++] : (BotTarget > 0 ? SpawnBot() : null);
            if (bot is null) continue;
            CreateGame(idleHumans[hi], bot);
        }

        // 3) Свободные боты играют между собой (живость арены), не занимая место будущего соперника.
        for (; bi + 1 < idleBots.Count; bi += 2) CreateGame(idleBots[bi], idleBots[bi + 1]);
    }

    private string SpawnBot()
    {
        _botCounter++;
        var key = $"bot-{Id}-{_botCounter}";
        var persona = BotPersona.For(key);
        _players[key] = new Player
        {
            // Имя без эмодзи-префикса — бот-ность несёт флаг IsBot (в UI рисуется тег «бот»).
            Name = $"{BotNames[(_botCounter - 1) % BotNames.Length]} ({persona.Rating})",
            IsBot = true,
            Rating = persona.Rating,
            Skill = persona.Skill,
            SpeedFactor = persona.Speed,
            WaitingSince = DateTimeOffset.UtcNow,
        };
        _dirty = true;
        return key;
    }

    private void CreateGame(string a, string b)
    {
        bool aWhite = _gameCounter % 2 == 0;
        var gid = $"{Id}-g{_gameCounter++}";
        _games[gid] = new Game
        {
            Id = gid,
            WhiteSub = aWhite ? a : b,
            WhiteName = _players[aWhite ? a : b].Name,
            BlackSub = aWhite ? b : a,
            BlackName = _players[aWhite ? b : a].Name,
            WhiteMs = _tc.InitialSeconds * 1000L,
            BlackMs = _tc.InitialSeconds * 1000L,
            LastMoveAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow
        };
        foreach (var s in new[] { a, b })
        {
            var p = _players[s];
            // Метрика ликвидности матчмейкинга: сколько человек ждал и достался ли ему бот.
            if (!p.IsBot)
            {
                var opp = _players[s == a ? b : a];
                analytics.Capture("arena_paired", s, new Dictionary<string, object?>
                {
                    ["tournament_id"] = Id,
                    ["time_control"] = _tc.ToString(),
                    ["opponent_is_bot"] = opp.IsBot,
                    ["wait_seconds"] = p.WaitingSince is { } w ? (int)(DateTimeOffset.UtcNow - w).TotalSeconds : 0,
                });
            }
            p.Playing = true;
            p.GameId = gid;
            p.Seeking = false; // спарен — больше не в пуле подбора
            p.WaitingSince = null;
        }
    }

    private bool DeductClock(Game g, Color mover)
    {
        var elapsed = (long)(DateTimeOffset.UtcNow - g.LastMoveAt).TotalMilliseconds;
        if (mover == Color.White)
        {
            if (g.WhiteMs - elapsed <= 0) { g.WhiteMs = 0; FlagTimeout(g, Color.White); return true; }
            g.WhiteMs -= elapsed;
        }
        else
        {
            if (g.BlackMs - elapsed <= 0) { g.BlackMs = 0; FlagTimeout(g, Color.Black); return true; }
            g.BlackMs -= elapsed;
        }
        g.LastMoveAt = DateTimeOffset.UtcNow;
        return false;
    }

    // Просрочка времени: поражение просрочившего — но если у соперника недостаточно материала для мата,
    // партия завершается вничью (FIDE 6.9 / lichess).
    private static void FlagTimeout(Game g, Color flagged)
    {
        bool winnerIsWhite = flagged == Color.Black;
        if (ChessMaterial.HasMatingMaterial(g.Board.Fen, winnerIsWhite))
        {
            g.Result = winnerIsWhite ? GameResult.WhiteWins : GameResult.BlackWins;
            g.Reason = GameEndReason.Timeout;
        }
        else
        {
            g.Result = GameResult.Draw;
            g.Reason = GameEndReason.InsufficientMaterial; // ничья: у соперника нет материала на мат
        }
    }

    private void FinishGame(Game g)
    {
        if (g.Status != GameStatus.InProgress) return;
        g.Status = GameStatus.Finished;
        g.FinishedAt = DateTimeOffset.UtcNow;

        var white = _players[g.WhiteSub];
        var black = _players[g.BlackSub];
        Award(white, g.Result == GameResult.WhiteWins ? 1.0 : g.Result == GameResult.Draw ? 0.5 : 0.0);
        Award(black, g.Result == GameResult.BlackWins ? 1.0 : g.Result == GameResult.Draw ? 0.5 : 0.0);

        // Победа в берсерк-партии — бонус +2 сверх обычного начисления.
        if (g.Result == GameResult.WhiteWins && g.WhiteBerserk) white.Score += 2;
        if (g.Result == GameResult.BlackWins && g.BlackBerserk) black.Score += 2;

        // Событие по каждому игроку-человеку (ядро вовлечённости: сыгранные партии, исход, берсерк, бот-ли соперник).
        var durationSec = g.StartedAt == default ? null : (int?)(g.FinishedAt!.Value - g.StartedAt).TotalSeconds;
        foreach (var (sub, isWhite) in new[] { (g.WhiteSub, true), (g.BlackSub, false) })
        {
            var p = _players[sub];
            if (p.IsBot) continue;
            var outcome = g.Result == GameResult.Draw ? "draw"
                : (g.Result == GameResult.WhiteWins) == isWhite ? "win" : "loss";
            analytics.Capture("arena_game_finished", sub, new Dictionary<string, object?>
            {
                ["tournament_id"] = Id,
                ["time_control"] = _tc.ToString(),
                ["result"] = outcome,
                ["reason"] = g.Reason.ToString(),
                ["opponent_is_bot"] = _players[isWhite ? g.BlackSub : g.WhiteSub].IsBot,
                ["was_berserk"] = isWhite ? g.WhiteBerserk : g.BlackBerserk,
                ["duration_seconds"] = durationSec,
            });
        }

        _dirty = true; // изменилась таблица — сохранить, чтобы пережить деактивацию грейна

        ArchiveFinishedGame(g);
    }

    /// <summary>Архивирует партию в ApiService (история/разбор) — fire-and-forget, не блокирует ход турнира.
    /// Клиент берём опционально из DI (тестовый силос его не регистрирует — тогда просто пропускаем).</summary>
    private void ArchiveFinishedGame(Game g)
    {
        var archive = services.GetService<IArenaGameArchiveClient>();
        if (archive is null) return;
        var req = new ArenaGameArchiveRequest(
            Id, g.Id, g.WhiteSub, g.BlackSub, g.WhiteName, g.BlackName,
            IsBotSub(g.WhiteSub), IsBotSub(g.BlackSub),
            g.Board.Pgn, g.Result, g.Reason, _tc, g.FinishedAt ?? DateTimeOffset.UtcNow);
        _ = archive.ArchiveAsync(req); // ошибки клиент логирует сам и не бросает
    }

    private static void Award(Player p, double outcome)
    {
        var before = p.Score;
        (p.Score, p.Streak) = ArenaScoring.Apply(p.Score, p.Streak, outcome);
        p.Games++;
        if (outcome == 1.0) p.Wins++;
        p.Results.Add(p.Score - before); // 0 — поражение, 1/2 — ничья, 2/4 — победа (×2 на огне)
    }

    private ArenaGameDto BuildGameDto(Game g, string sub)
    {
        bool iAmWhite = sub == g.WhiteSub;
        bool myMoved = iAmWhite ? g.WhiteMoved : g.BlackMoved;
        bool myBerserk = iAmWhite ? g.WhiteBerserk : g.BlackBerserk;
        bool canBerserk = g.Status == GameStatus.InProgress && !myMoved && !myBerserk;

        return new ArenaGameDto(
            g.Id, g.Board.Fen, iAmWhite ? Color.White : Color.Black,
            g.Board.Turn, g.WhiteName, g.BlackName, g.WhiteMs, g.BlackMs,
            g.Status, g.Result, g.Board.LastSan,
            g.WhiteBerserk, g.BlackBerserk, canBerserk,
            g.Board.LastFrom, g.Board.LastTo, g.Board.CheckSquare,
            IsBotSub(g.WhiteSub), IsBotSub(g.BlackSub));
    }
}
