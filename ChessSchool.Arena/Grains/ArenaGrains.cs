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
    Task<ArenaGameDto?> MoveAsync(string sub, MoveInput move);
    Task BerserkAsync(string sub);
    Task ResignAsync(string sub);
}

// ----------------------- Каталог / расписание -----------------------

public sealed class ArenaDirectoryGrain(IGrainFactory grains) : Grain, IArenaDirectoryGrain
{
    private sealed record Slot(string Id, string Name, TimeControl Tc, DateTimeOffset StartsAt, int DurationSeconds);

    // Описание повторяющихся серий: тип, тайм-контроль, шаг (часы), длительность (сек), смещение (мин).
    private static readonly (string Type, TimeControl Tc, int StepHours, int DurationSec, int OffsetMin)[] Series =
    [
        ("Bullet", new TimeControl(60, 0), 3, 3600, 0),
        ("Blitz", new TimeControl(180, 0), 1, 3600, 0),   // блиц каждый час — непрерывная лента
        ("Rapid", new TimeControl(600, 0), 3, 5400, 30),
    ];

    private const int WindowBackHours = 3;
    private const int WindowAheadHours = 6;

    public async Task<IReadOnlyList<TournamentSummaryDto>> ListAsync()
    {
        var now = DateTimeOffset.Now;
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset)
            .AddHours(-WindowBackHours);
        var windowEnd = windowStart.AddHours(WindowBackHours + WindowAheadHours);

        var slots = new List<Slot>();
        foreach (var (type, tc, stepHours, durationSec, offsetMin) in Series)
        {
            for (var t = windowStart.AddMinutes(offsetMin); t < windowEnd; t = t.AddHours(stepHours))
            {
                var id = $"{type.ToLowerInvariant()}-{t.ToUnixTimeSeconds()}";
                var name = $"{type} {tc} {t:HH:mm}";
                slots.Add(new Slot(id, name, tc, t, durationSec));
            }
        }

        // Конфигурируем грейны параллельно и собираем карточки.
        var tasks = slots.Select(async s =>
        {
            var g = grains.GetGrain<IArenaTournamentGrain>(s.Id);
            bool finished = s.StartsAt.AddSeconds(s.DurationSeconds) <= now;
            if (finished)
                await g.ConfigureFinishedDemoAsync(s.Name, s.Tc, s.StartsAt, s.DurationSeconds);
            else
                await g.ConfigureAsync(s.Name, s.Tc, s.StartsAt, s.DurationSeconds);
            return await g.GetSummaryAsync();
        });

        var list = await Task.WhenAll(tasks);
        return list.OrderBy(t => t.StartsAt).ToList();
    }
}

// ----------------------- Турнир -----------------------

public sealed class ArenaTournamentGrain(ArenaNotifier notifier, IChessEngine engine) : Grain, IArenaTournamentGrain
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
    private const int BotSkill = 5;        // уровень Stockfish (0..20)
    private const int BotMoveTimeMs = 300; // лимит на ход бота
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
    }

    private bool _configured;
    private string _name = "";
    private TimeControl _tc = TimeControl.Blitz;
    private int _durationSeconds;
    private DateTimeOffset _startsAt;
    private int _gameCounter;
    private IDisposable? _timer;

    private readonly Dictionary<string, Player> _players = new();
    private readonly Dictionary<string, Game> _games = new();

    private string Id => this.GetPrimaryKeyString();

    private TournamentStatus Status()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _startsAt) return TournamentStatus.Created;
        if (now < _startsAt.AddSeconds(_durationSeconds)) return TournamentStatus.Running;
        return TournamentStatus.Finished;
    }

    public Task ConfigureAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds)
    {
        if (_configured) return Task.CompletedTask;
        _configured = true;
        _name = name;
        _tc = tc;
        _startsAt = startsAt.ToUniversalTime();
        _durationSeconds = durationSeconds;
        EnsureTimer();
        return Task.CompletedTask;
    }

    /// <summary>Сидирует завершённый турнир с готовой таблицей — для демонстрации страницы результатов.</summary>
    public Task ConfigureFinishedDemoAsync(string name, TimeControl tc, DateTimeOffset startsAt, int durationSeconds)
    {
        if (_configured) return Task.CompletedTask;
        _configured = true;
        _name = name;
        _tc = tc;
        _startsAt = startsAt.ToUniversalTime();
        _durationSeconds = durationSeconds;

        AddDemoPlayer("ArenaHost_0", 20, 0, 14, 8, [2, 2, 0, 0, 0, 2, 2, 4, 0, 2, 2, 4, 0, 2]);
        AddDemoPlayer("French_Winawer", 15, 2, 15, 6, [2, 1, 0, 1, 2, 1, 2, 2, 0, 0, 2, 0, 0, 2, 2]);
        AddDemoPlayer("DeepBlue_v2", 13, 0, 15, 5, [0, 1, 2, 1, 0, 1, 0, 0, 2, 2, 0, 2, 2, 0, 0]);
        AddDemoPlayer("Stockfish_15", 12, 0, 14, 5, [0, 0, 2, 2, 4, 0, 0, 0, 2, 0, 0, 0, 2, 0]);
        return Task.CompletedTask;
    }

    private void AddDemoPlayer(string name, int score, int streak, int games, int wins, int[] results)
    {
        var p = new Player { Name = name, Score = score, Streak = streak, Games = games, Wins = wins };
        p.Results.AddRange(results);
        _players[name] = p;
    }

    private void EnsureTimer()
    {
        if (_timer is null && Status() == TournamentStatus.Running)
            _timer = this.RegisterGrainTimer(OnTimerAsync, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private async Task OnTimerAsync()
    {
        Tick();
        await DriveBotsAsync(); // ходы ботов через Stockfish (серверная игра)
        notifier.Notify(Id);
    }

    public Task JoinAsync(string sub, string name)
    {
        // Регистрация возможна и до старта (Created), и во время турнира (Running).
        if (Status() == TournamentStatus.Finished) return Task.CompletedTask;
        if (!_players.ContainsKey(sub))
            _players[sub] = new Player { Name = name, WaitingSince = DateTimeOffset.UtcNow };
        EnsureTimer();
        Tick();
        notifier.Notify(Id);
        return Task.CompletedTask;
    }

    public Task<TournamentSummaryDto> GetSummaryAsync()
    {
        Tick();
        EnsureTimer();
        return Task.FromResult(new TournamentSummaryDto(
            Id, _name, _tc, Status(), _players.Count, SecondsLeft(),
            _players.Values.Count(p => p.IsBot), _startsAt, _durationSeconds));
    }

    public Task<ArenaStateDto> GetStateAsync(string sub)
    {
        Tick();
        EnsureTimer();

        var standings = _players
            .OrderByDescending(p => p.Value.Score)
            .ThenByDescending(p => p.Value.Streak)
            .Select((p, i) => new ArenaStandingRow(i + 1, p.Value.Name, p.Value.Score, p.Value.Streak,
                p.Value.OnFire, p.Value.Playing, p.Value.Games, p.Value.Wins, p.Value.Results.ToList()))
            .ToList();

        ArenaGameDto? myGame = null;
        if (_players.TryGetValue(sub, out var me) && me.GameId is { } gid && _games.TryGetValue(gid, out var g))
            myGame = BuildGameDto(g, sub);

        return Task.FromResult(new ArenaStateDto(
            Id, _name, Status(), SecondsLeft(), _players.ContainsKey(sub),
            _players.TryGetValue(sub, out var p2) ? p2.Score : 0, standings, myGame,
            _tc, _startsAt, _durationSeconds, _players.Values.Count(p => p.IsBot)));
    }

    public Task<ArenaGameDto?> MoveAsync(string sub, MoveInput move)
    {
        if (!_players.TryGetValue(sub, out var player) || player.GameId is null)
            return Task.FromResult<ArenaGameDto?>(null);
        if (!_games.TryGetValue(player.GameId, out var game) || game.Status != GameStatus.InProgress)
            return Task.FromResult<ArenaGameDto?>(null);

        var mover = sub == game.WhiteSub ? Color.White : Color.Black;
        if (mover != game.Board.Turn) return Task.FromResult<ArenaGameDto?>(null);

        if (DeductClock(game, mover)) { FinishGame(game); notifier.Notify(Id); return Task.FromResult<ArenaGameDto?>(BuildGameDto(game, sub)); }

        if (!game.Board.TryMove(move.From, move.To, move.Promotion))
            return Task.FromResult<ArenaGameDto?>(BuildGameDto(game, sub));

        if (mover == Color.White) { game.WhiteMoved = true; if (!game.WhiteBerserk) game.WhiteMs += _tc.IncrementSeconds * 1000L; }
        else { game.BlackMoved = true; if (!game.BlackBerserk) game.BlackMs += _tc.IncrementSeconds * 1000L; }
        game.LastMoveAt = DateTimeOffset.UtcNow;

        if (game.Board.IsEndGame)
        {
            (game.Result, game.Reason) = game.Board.Resolve();
            FinishGame(game);
        }

        notifier.Notify(Id);
        return Task.FromResult<ArenaGameDto?>(BuildGameDto(game, sub));
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

    public Task ResignAsync(string sub)
    {
        if (_players.TryGetValue(sub, out var player) && player.GameId is { } gid
            && _games.TryGetValue(gid, out var game) && game.Status == GameStatus.InProgress)
        {
            game.Result = sub == game.WhiteSub ? GameResult.BlackWins : GameResult.WhiteWins;
            game.Reason = GameEndReason.Resignation;
            FinishGame(game);
            notifier.Notify(Id);
        }
        return Task.CompletedTask;
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
            && g.FinishedAt is { } f && (now - f).TotalSeconds > 3).ToList())
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
        }

        if (bots > targetBots)
        {
            foreach (var kv in _players.Where(kv => kv.Value.IsBot && !kv.Value.Playing).ToList())
            {
                if (bots <= targetBots) break;
                _players.Remove(kv.Key);
                bots--;
            }
        }
    }

    private static string BotName(int n) => $"🤖 {BotNames[(n - 1) % BotNames.Length]}";

    private async Task DriveBotsAsync()
    {
        foreach (var g in _games.Values.Where(g => g.Status == GameStatus.InProgress).ToList())
        {
            var botColor = g.Board.Turn;
            var moverSub = botColor == Color.White ? g.WhiteSub : g.BlackSub;
            if (!_players.TryGetValue(moverSub, out var mp) || !mp.IsBot) continue;

            var uci = await engine.GetBestMoveAsync(g.Board.Fen, BotSkill, BotMoveTimeMs);
            bool moved = uci is not null && ApplyUci(g, uci);
            if (!moved) moved = g.Board.TryMakeRandomMove();
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
