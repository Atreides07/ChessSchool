using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.Arena.Grains;

// ----------------------- Интерфейсы грейнов -----------------------

/// <summary>Каталог турниров (синглтон, ключ 0). Сидит демо-турниры и отдаёт список.</summary>
public interface IArenaDirectoryGrain : IGrainWithIntegerKey
{
    Task<IReadOnlyList<TournamentSummaryDto>> ListAsync();
}

/// <summary>
/// Грейн одного арена-турнира (ключ = id). Непрерывный пейринг, очки со «стриками» и berserk.
/// Тик выполняется по grain-таймеру (раз в секунду) и пушит изменения подписчикам через ArenaNotifier.
/// </summary>
public interface IArenaTournamentGrain : IGrainWithStringKey
{
    Task ConfigureAsync(string name, TimeControl tc, int durationSeconds);
    Task ConfigureFinishedDemoAsync(string name, TimeControl tc, DateTimeOffset startedAt, int durationSeconds);
    Task JoinAsync(string sub, string name);
    Task<ArenaStateDto> GetStateAsync(string sub);
    Task<TournamentSummaryDto> GetSummaryAsync();
    Task<ArenaGameDto?> MoveAsync(string sub, MoveInput move);
    Task BerserkAsync(string sub);
    Task ResignAsync(string sub);
}

// ----------------------- Каталог -----------------------

public sealed class ArenaDirectoryGrain(IGrainFactory grains) : Grain, IArenaDirectoryGrain
{
    private static readonly (string Id, string Name, TimeControl Tc, int Minutes)[] Seed =
    [
        ("bullet-arena", "Пуля (Bullet)", TimeControl.Bullet, 15),
        ("blitz-arena",  "Вечерний блиц", TimeControl.Blitz, 30),
        ("rapid-arena",  "Рапид",         TimeControl.Rapid, 45),
    ];

    public async Task<IReadOnlyList<TournamentSummaryDto>> ListAsync()
    {
        var list = new List<TournamentSummaryDto>();
        foreach (var (id, name, tc, minutes) in Seed)
        {
            var t = grains.GetGrain<IArenaTournamentGrain>(id);
            await t.ConfigureAsync(name, tc, minutes * 60);
            list.Add(await t.GetSummaryAsync());
        }

        // Демонстрационный завершённый турнир — чтобы можно было посмотреть результаты/участников.
        var finished = grains.GetGrain<IArenaTournamentGrain>("blitz-evening");
        await finished.ConfigureFinishedDemoAsync("Blitz 3+0 22:00", new TimeControl(180, 0),
            DateTimeOffset.Now.AddHours(-2), 3600);
        list.Add(await finished.GetSummaryAsync());

        return list;
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
        public DateTimeOffset? WaitingSince; // когда игрок встал в очередь на соперника
        public int Games;
        public int Wins;
        public readonly List<int> Results = new(); // очки за каждую сыгранную партию (0/1/2/4)
        public bool OnFire => Streak >= 2;
    }

    // Через сколько секунд ожидания соперника-человека подключается бот.
    private const int BotJoinSeconds = 15;
    private const int BotSkill = 5;        // уровень Stockfish (0..20) — умеренный, обыгрываемый
    private const int BotMoveTimeMs = 300; // лимит на ход бота
    private int _botCounter;

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
    private DateTimeOffset _startedAt;
    private TournamentStatus _status = TournamentStatus.Created;
    private int _gameCounter;
    private IDisposable? _timer;

    private readonly Dictionary<string, Player> _players = new();
    private readonly Dictionary<string, Game> _games = new();

    private string Id => this.GetPrimaryKeyString();

    public Task ConfigureAsync(string name, TimeControl tc, int durationSeconds)
    {
        if (_configured) return Task.CompletedTask;
        _configured = true;
        _name = name;
        _tc = tc;
        _durationSeconds = durationSeconds;
        _startedAt = DateTimeOffset.UtcNow;
        _status = TournamentStatus.Running;

        // Серверный драйвер: пейринг, таймауты и push-уведомления раз в секунду.
        _timer = this.RegisterGrainTimer(OnTimerAsync, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    /// <summary>Сидирует завершённый турнир с готовой таблицей — для демонстрации страницы результатов.</summary>
    public Task ConfigureFinishedDemoAsync(string name, TimeControl tc, DateTimeOffset startedAt, int durationSeconds)
    {
        if (_configured) return Task.CompletedTask;
        _configured = true;
        _name = name;
        _tc = tc;
        _startedAt = startedAt;
        _durationSeconds = durationSeconds;
        _status = TournamentStatus.Finished;

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

    private async Task OnTimerAsync()
    {
        if (!_configured) return;
        Tick();
        await DriveBotsAsync(); // ходы ботов через Stockfish (серверная игра)
        notifier.Notify(Id);
    }

    public Task JoinAsync(string sub, string name)
    {
        if (!_players.ContainsKey(sub))
            _players[sub] = new Player { Name = name, WaitingSince = DateTimeOffset.UtcNow };
        Tick();
        notifier.Notify(Id);
        return Task.CompletedTask;
    }

    public Task<TournamentSummaryDto> GetSummaryAsync()
    {
        Tick();
        return Task.FromResult(new TournamentSummaryDto(Id, _name, _tc, _status, _players.Count, SecondsLeft()));
    }

    public Task<ArenaStateDto> GetStateAsync(string sub)
    {
        Tick();

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
            Id, _name, _status, SecondsLeft(), _players.ContainsKey(sub),
            _players.TryGetValue(sub, out var p2) ? p2.Score : 0, standings, myGame,
            _tc, _startedAt, _durationSeconds));
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

        // Инкремент не начисляется berserk-игроку.
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
            // Berserk доступен, пока игрок не сделал свой первый ход: время пополам, без инкремента.
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

    private int SecondsLeft() => _status == TournamentStatus.Running
        ? Math.Max(0, _durationSeconds - (int)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds)
        : 0;

    private void Tick()
    {
        if (!_configured || _status == TournamentStatus.Finished) return;

        if (SecondsLeft() == 0) _status = TournamentStatus.Finished;

        // Часы и таймауты для всех идущих партий.
        foreach (var g in _games.Values.Where(g => g.Status == GameStatus.InProgress).ToList())
            if (DeductClock(g, g.Board.Turn)) FinishGame(g);

        var now = DateTimeOffset.UtcNow;
        foreach (var g in _games.Values.Where(g => g.Status != GameStatus.InProgress
            && g.FinishedAt is { } f && (now - f).TotalSeconds > 3).ToList())
        {
            // Боты эфемерны: после партии удаляются, люди — освобождаются для нового соперника.
            foreach (var s in new[] { g.WhiteSub, g.BlackSub })
                if (_players.TryGetValue(s, out var p) && p.IsBot) _players.Remove(s);
                else FreePlayer(s);
            _games.Remove(g.Id);
        }

        if (_status == TournamentStatus.Running)
        {
            EnsureBotsForWaiters();
            PairIdlePlayers();
        }
    }

    /// <summary>Подключает бота, если человек ждёт соперника дольше порога и пара иначе не сложится.</summary>
    private void EnsureBotsForWaiters()
    {
        var now = DateTimeOffset.UtcNow;
        int idleTotal = _players.Count(kv => !kv.Value.Playing);
        int longWaiters = _players.Values.Count(p => !p.Playing && !p.IsBot
            && p.WaitingSince is { } w && (now - w).TotalSeconds >= BotJoinSeconds);

        while (longWaiters > 0 && idleTotal % 2 == 1)
        {
            _botCounter++;
            _players[$"bot-{Id}-{_botCounter}"] = new Player
            {
                Name = $"🤖 CPU-{_botCounter}",
                IsBot = true,
                WaitingSince = now
            };
            idleTotal++;
            longWaiters--;
        }
    }

    /// <summary>Ходы ботов: лучший ход от Stockfish (серверный движок), при недоступности — случайный легальный.</summary>
    private async Task DriveBotsAsync()
    {
        foreach (var g in _games.Values.Where(g => g.Status == GameStatus.InProgress).ToList())
        {
            var botColor = g.Board.Turn;
            var moverSub = botColor == Color.White ? g.WhiteSub : g.BlackSub;
            if (!_players.TryGetValue(moverSub, out var mp) || !mp.IsBot) continue;

            var uci = await engine.GetBestMoveAsync(g.Board.Fen, BotSkill, BotMoveTimeMs);
            bool moved = uci is not null && ApplyUci(g, uci);
            if (!moved) moved = g.Board.TryMakeRandomMove(); // fallback, если движок недоступен/вернул мусор
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
            p.WaitingSince = DateTimeOffset.UtcNow; // снова в очереди
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

        // Бонус berserk: +1 очко за победу с заряженными часами.
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
