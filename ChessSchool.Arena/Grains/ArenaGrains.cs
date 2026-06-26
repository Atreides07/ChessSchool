using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.Arena.Grains;

// ----------------------- Интерфейсы грейнов -----------------------

/// <summary>Каталог турниров (синглтон, ключ 0). Генерирует расписание и отдаёт список.</summary>
public interface IArenaDirectoryGrain : IGrainWithIntegerKey
{
    Task<IReadOnlyList<TournamentSummaryDto>> ListAsync();
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
    Task JoinAsync(string sub, string name);
    Task<ArenaStateDto> GetStateAsync(string sub);
    Task<TournamentSummaryDto> GetSummaryAsync();
    Task<IReadOnlyList<ArenaBoardDto>> GetBoardsAsync();
    Task<ArenaGameDto?> MoveAsync(string sub, MoveInput move);
    Task BerserkAsync(string sub);
    Task ResignAsync(string sub);
}

// ----------------------- Каталог / расписание -----------------------

public sealed class ArenaDirectoryGrain(IGrainFactory grains) : Grain, IArenaDirectoryGrain
{
    public async Task<IReadOnlyList<TournamentSummaryDto>> ListAsync()
    {
        var now = DateTimeOffset.Now;
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset)
            .AddHours(-ArenaSchedule.WindowBackHours);
        var windowEnd = windowStart.AddHours(ArenaSchedule.WindowBackHours + ArenaSchedule.WindowAheadHours);

        var ids = new List<string>();
        foreach (var spec in ArenaSchedule.Series)
            for (var t = windowStart.AddMinutes(spec.OffsetMin); t < windowEnd; t = t.AddHours(spec.StepHours))
                ids.Add(ArenaSchedule.MakeId(spec.Type, t));

        // Грейн сам конфигурируется из своего id (см. EnsureConfigured) — каталогу достаточно
        // запросить карточку. Делаем это параллельно.
        var tasks = ids.Select(id => grains.GetGrain<IArenaTournamentGrain>(id).GetSummaryAsync());
        var list = await Task.WhenAll(tasks);
        return list.OrderBy(t => t.StartsAt).ToList();
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
    IChessEngine engine) : Grain, IArenaTournamentGrain
{
    private sealed class Player
    {
        public string Name = "";
        public int Score;
        public int Streak;
        public bool Playing;
        public string? GameId;
        public bool IsBot;
        public DateTimeOffset? WaitingSince;
        public int Games;
        public int Wins;
        public readonly List<int> Results = new(); // очки за каждую сыгранную партию (0/1/2/4)
        public bool OnFire => Streak >= 2;
    }

    // Минимум участников в идущем турнире — добираем ботами; при достатке людей боты убираются.
    private const int MinParticipants = 6;
    private const int BotSkill = 5; // уровень Stockfish (0..20)
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
    }

    private async Task OnTimerAsync()
    {
        Tick();
        await DriveBotsAsync(); // ходы ботов через Stockfish (серверная игра)
        await FlushAsync();
        notifier.Notify(Id);
    }

    public async Task JoinAsync(string sub, string name)
    {
        EnsureConfigured();
        // Регистрация возможна и до старта (Created), и во время турнира (Running).
        if (Status() == TournamentStatus.Finished) return;
        if (!_players.ContainsKey(sub))
        {
            _players[sub] = new Player { Name = name, WaitingSince = DateTimeOffset.UtcNow };
            _dirty = true;
        }
        EnsureTimer();
        Tick();
        await FlushAsync();
        notifier.Notify(Id);
    }

    public async Task<TournamentSummaryDto> GetSummaryAsync()
    {
        EnsureConfigured();
        Tick();
        EnsureTimer();
        await FlushAsync();
        return new TournamentSummaryDto(
            Id, _name, _tc, Status(), _players.Count, SecondsLeft(),
            _players.Values.Count(p => p.IsBot), _startsAt, _durationSeconds);
    }

    public async Task<ArenaStateDto> GetStateAsync(string sub)
    {
        EnsureConfigured();
        Tick();
        EnsureTimer();
        await FlushAsync();

        var standings = _players
            .OrderByDescending(p => p.Value.Score)
            .ThenByDescending(p => p.Value.Streak)
            .Select((p, i) => new ArenaStandingRow(i + 1, p.Value.Name, p.Value.Score, p.Value.Streak,
                p.Value.OnFire, p.Value.Playing, p.Value.Games, p.Value.Wins, p.Value.Results.ToList()))
            .ToList();

        ArenaGameDto? myGame = null;
        if (_players.TryGetValue(sub, out var me) && me.GameId is { } gid && _games.TryGetValue(gid, out var g))
            myGame = BuildGameDto(g, sub);

        return new ArenaStateDto(
            Id, _name, Status(), SecondsLeft(), _players.ContainsKey(sub),
            _players.TryGetValue(sub, out var p2) ? p2.Score : 0, standings, myGame,
            _tc, _startsAt, _durationSeconds, _players.Values.Count(p => p.IsBot),
            BuildBoards(4)); // в шапке турнира — только 4 доски, остальное на /games
    }

    public async Task<IReadOnlyList<ArenaBoardDto>> GetBoardsAsync()
    {
        EnsureConfigured();
        Tick();
        EnsureTimer();
        await FlushAsync();
        return BuildBoards(int.MaxValue); // все доски — для страницы «Все игры»
    }

    /// <summary>Трансляция «идёт сейчас»: активные + только что завершённые партии (с финальным счётом).</summary>
    private IReadOnlyList<ArenaBoardDto> BuildBoards(int take)
    {
        var now = DateTimeOffset.UtcNow;
        return _games.Values
            .Where(g => g.Status == GameStatus.InProgress
                || (g.FinishedAt is { } f && (now - f).TotalSeconds <= 6))
            .OrderByDescending(g => g.Status == GameStatus.InProgress)
            .ThenByDescending(g => ScoreOf(g.WhiteSub) + ScoreOf(g.BlackSub))
            .Take(take)
            .Select(g => new ArenaBoardDto(
                g.Id, g.Board.Fen, g.WhiteName, g.BlackName,
                ScoreOf(g.WhiteSub), ScoreOf(g.BlackSub),
                g.WhiteMs, g.BlackMs, g.Board.Turn, g.Status, g.Result,
                g.Board.LastFrom, g.Board.LastTo, g.Board.CheckSquare))
            .ToList();
    }

    private int ScoreOf(string sub) => _players.TryGetValue(sub, out var p) ? p.Score : 0;

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
            return;
        }
        if (status != TournamentStatus.Running) return; // Created — только регистрация

        // Часы и таймауты для всех идущих партий.
        foreach (var g in _games.Values.Where(g => g.Status == GameStatus.InProgress).ToList())
            if (DeductClock(g, g.Board.Turn)) FinishGame(g);

        var now = DateTimeOffset.UtcNow;
        foreach (var g in _games.Values.Where(g => g.Status != GameStatus.InProgress
            && g.FinishedAt is { } f && (now - f).TotalSeconds > 6).ToList())
        {
            foreach (var s in new[] { g.WhiteSub, g.BlackSub }) FreePlayer(s);
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
        int humans = _players.Values.Count(p => !p.IsBot);
        int bots = _players.Values.Count(p => p.IsBot);

        int targetBots = Math.Max(0, MinParticipants - humans);
        // Пока людей меньше минимума — держим чётное число участников (для пейринга).
        if (humans < MinParticipants && (humans + targetBots) % 2 == 1) targetBots++;

        while (bots < targetBots)
        {
            _botCounter++;
            var key = $"bot-{Id}-{_botCounter}";
            _players[key] = new Player { Name = BotName(_botCounter), IsBot = true, WaitingSince = DateTimeOffset.UtcNow };
            bots++;
            _dirty = true;
        }

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

    private static string BotName(int n) => $"🤖 {BotNames[(n - 1) % BotNames.Length]}";

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
                g.BotPlannedMs = BotThinkMs(g);
                g.BotThinkUntil = now.AddMilliseconds(g.BotPlannedMs);
                continue;
            }
            if (now < g.BotThinkUntil.Value) continue; // ещё думает

            // Движку даём время, пропорциональное сложности (но без блокировки грейна надолго).
            int engineMs = Math.Clamp(g.BotPlannedMs, 120, 500);
            var uci = await engine.GetBestMoveAsync(g.Board.Fen, BotSkill, engineMs);
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
    private static int BotThinkMs(Game g)
    {
        int options = g.Board.LegalMoveCount;
        if (options <= 1) return 150; // ходить нечем кроме одного — не думаем

        int baseMs = g.Board.InCheck ? 300 : 220 + options * 55; // больше выбора — дольше
        baseMs = Math.Min(baseMs, 2400);
        int jitter = Random.Shared.Next(-120, 350);
        return Math.Clamp(baseMs + jitter, 150, 2800);
    }

    private static bool ApplyUci(Game g, string uci)
    {
        if (uci.Length < 4) return false;
        var from = uci[..2];
        var to = uci[2..4];
        var promo = uci.Length > 4 ? uci[4].ToString() : null;
        return g.Board.TryMove(from, to, promo);
    }

    private void FreePlayer(string sub)
    {
        if (_players.TryGetValue(sub, out var p))
        {
            p.Playing = false;
            p.GameId = null;
            p.WaitingSince = DateTimeOffset.UtcNow;
        }
    }

    private void PairIdlePlayers()
    {
        var idle = _players.Where(kv => !kv.Value.Playing)
            .OrderByDescending(kv => kv.Value.Score)
            .Select(kv => kv.Key).ToList();

        for (int i = 0; i + 1 < idle.Count; i += 2)
        {
            string a = idle[i], b = idle[i + 1];
            bool aWhite = _gameCounter % 2 == 0;
            var gid = $"{Id}-g{_gameCounter++}";
            var game = new Game
            {
                Id = gid,
                WhiteSub = aWhite ? a : b,
                WhiteName = _players[aWhite ? a : b].Name,
                BlackSub = aWhite ? b : a,
                BlackName = _players[aWhite ? b : a].Name,
                WhiteMs = _tc.InitialSeconds * 1000L,
                BlackMs = _tc.InitialSeconds * 1000L,
                LastMoveAt = DateTimeOffset.UtcNow
            };
            _games[gid] = game;
            foreach (var s in new[] { a, b })
            {
                _players[s].Playing = true;
                _players[s].GameId = gid;
                _players[s].WaitingSince = null;
            }
        }
    }

    private bool DeductClock(Game g, Color mover)
    {
        var elapsed = (long)(DateTimeOffset.UtcNow - g.LastMoveAt).TotalMilliseconds;
        if (mover == Color.White)
        {
            if (g.WhiteMs - elapsed <= 0) { g.WhiteMs = 0; g.Result = GameResult.BlackWins; g.Reason = GameEndReason.Timeout; return true; }
            g.WhiteMs -= elapsed;
        }
        else
        {
            if (g.BlackMs - elapsed <= 0) { g.BlackMs = 0; g.Result = GameResult.WhiteWins; g.Reason = GameEndReason.Timeout; return true; }
            g.BlackMs -= elapsed;
        }
        g.LastMoveAt = DateTimeOffset.UtcNow;
        return false;
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

        if (g.Result == GameResult.WhiteWins && g.WhiteBerserk) white.Score += 1;
        if (g.Result == GameResult.BlackWins && g.BlackBerserk) black.Score += 1;

        _dirty = true; // изменилась таблица — сохранить, чтобы пережить деактивацию грейна
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
            g.Board.LastFrom, g.Board.LastTo, g.Board.CheckSquare);
    }
}
