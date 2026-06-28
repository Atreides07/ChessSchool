using Chess;
using ChessSchool.Contracts;
using ChessSchool.GameServer.Services;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.GameServer.Grains;

public sealed class GameGrain(IGameArchiveClient archive, ILogger<GameGrain> logger, IAnalytics analytics) : Grain, IGameGrain
{
    private ChessBoard _board = new();
    private string _whiteSub = "", _whiteName = "", _blackSub = "", _blackName = "";
    private TimeControl _tc = TimeControl.Blitz;
    private long _whiteMs, _blackMs;
    private DateTimeOffset _lastMoveAt;
    private GameStatus _status = GameStatus.WaitingForOpponent;
    private GameResult _result = GameResult.Ongoing;
    private GameEndReason _reason = GameEndReason.None;
    private string? _lastSan;
    private string? _lastFrom;
    private string? _lastTo;
    private string? _pendingPromotion;
    private bool _archived;

    private string GameId => this.GetPrimaryKeyString();

    public Task<GameStateDto> InitializeAsync(string whiteSub, string whiteName, string blackSub, string blackName, TimeControl tc)
    {
        _whiteSub = whiteSub; _whiteName = whiteName;
        _blackSub = blackSub; _blackName = blackName;
        _tc = tc;
        _whiteMs = _blackMs = tc.InitialSeconds * 1000L;
        _lastMoveAt = DateTimeOffset.UtcNow;
        _status = GameStatus.InProgress;
        _board = new ChessBoard();
        _board.OnPromotePawn += (_, e) => e.PromotionResult = MapPromotion(_pendingPromotion);
        return Task.FromResult(BuildState());
    }

    public async Task<MoveResult> TryMoveAsync(string playerSub, MoveInput input)
    {
        if (_status != GameStatus.InProgress)
            return new MoveResult(false, "Партия не идёт.", BuildState());

        Color? mover = playerSub == _whiteSub ? Color.White
            : playerSub == _blackSub ? Color.Black
            : null;
        if (mover is null) return new MoveResult(false, "Вы не участник этой партии.", BuildState());
        if (mover != CurrentTurn()) return new MoveResult(false, "Сейчас не ваш ход.", BuildState());

        // Списываем время с часов ходящего; при флаге партия завершается по таймауту.
        ApplyClock(mover.Value);
        if (_status == GameStatus.Finished)
        {
            await EndAsync();
            return new MoveResult(true, null, BuildState());
        }

        _pendingPromotion = input.Promotion;
        var move = new Move(input.From, input.To);
        if (!_board.IsValidMove(move))
            return new MoveResult(false, "Недопустимый ход.", BuildState());

        _board.Move(move);
        _lastSan = move.San;
        _lastFrom = Sq(move.OriginalPosition);
        _lastTo = Sq(move.NewPosition);
        if (mover == Color.White) _whiteMs += _tc.IncrementSeconds * 1000L;
        else _blackMs += _tc.IncrementSeconds * 1000L;
        _lastMoveAt = DateTimeOffset.UtcNow;

        if (_board.IsEndGame)
        {
            ResolveEndgame();
            await EndAsync();
        }

        return new MoveResult(true, null, BuildState());
    }

    public async Task<GameStateDto> ResignAsync(string playerSub)
    {
        if (_status == GameStatus.InProgress)
        {
            _result = playerSub == _whiteSub ? GameResult.BlackWins : GameResult.WhiteWins;
            _reason = GameEndReason.Resignation;
            _status = GameStatus.Finished;
            await EndAsync();
        }
        return BuildState();
    }

    public Task<GameStateDto?> GetStateAsync() =>
        Task.FromResult(_status == GameStatus.WaitingForOpponent ? null : BuildState());

    private Color CurrentTurn() => _board.Turn.AsChar == 'w' ? Color.White : Color.Black;

    private void ApplyClock(Color mover)
    {
        var elapsed = (long)(DateTimeOffset.UtcNow - _lastMoveAt).TotalMilliseconds;
        if (mover == Color.White)
        {
            _whiteMs -= elapsed;
            if (_whiteMs <= 0) { _whiteMs = 0; FlagTimeout(Color.White); }
        }
        else
        {
            _blackMs -= elapsed;
            if (_blackMs <= 0) { _blackMs = 0; FlagTimeout(Color.Black); }
        }
    }

    // Просрочка времени: поражение просрочившего — но если у соперника недостаточно материала для мата,
    // партия завершается вничью (FIDE 6.9 / lichess).
    private void FlagTimeout(Color flagged)
    {
        bool winnerIsWhite = flagged == Color.Black;
        if (ChessMaterial.HasMatingMaterial(_board.ToFen(), winnerIsWhite))
        {
            _result = winnerIsWhite ? GameResult.WhiteWins : GameResult.BlackWins;
            _reason = GameEndReason.Timeout;
        }
        else
        {
            _result = GameResult.Draw;
            _reason = GameEndReason.InsufficientMaterial; // ничья: у соперника нет материала на мат
        }
        _status = GameStatus.Finished;
    }

    private void ResolveEndgame()
    {
        _status = GameStatus.Finished;
        var eg = _board.EndGame;
        _result = eg?.WonSide is null ? GameResult.Draw
            : eg.WonSide.AsChar == 'w' ? GameResult.WhiteWins : GameResult.BlackWins;
        _reason = eg?.EndgameType.ToString() switch
        {
            "Checkmate" => GameEndReason.Checkmate,
            "Stalemate" => GameEndReason.Stalemate,
            "InsufficientMaterial" => GameEndReason.InsufficientMaterial,
            "Resigned" => GameEndReason.Resignation,
            _ => GameEndReason.DrawAgreed
        };
    }

    private async Task EndAsync()
    {
        if (_archived) return;
        _archived = true;
        try
        {
            await archive.ArchiveAsync(new ArchiveGameRequest(
                GameId, _whiteSub, _blackSub, _result, _reason, _board.ToPgn(), DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось заархивировать партию {GameId}", GameId);
        }
        analytics.Capture("online_game_finished", GameId, new Dictionary<string, object?>
        {
            ["result"] = _result.ToString(),
            ["reason"] = _reason.ToString(),
            ["move_count"] = _board.MoveIndex,
        });
        // Завершённую партию выгружаем из памяти — в RAM живут только активные (масштаб 1M).
        DeactivateOnIdle();
    }

    private GameStateDto BuildState() => new(
        GameId, _board.ToFen(), _whiteSub, _whiteName, _blackSub, _blackName,
        CurrentTurn(), _whiteMs, _blackMs, _lastSan, _status, _result, _reason,
        (_board.MoveIndex + 1) / 2 + 1, _lastFrom, _lastTo, CheckSquare());

    private string? CheckSquare() => _board.WhiteKingChecked ? Sq(_board.WhiteKing)
        : _board.BlackKingChecked ? Sq(_board.BlackKing)
        : null;

    private static string Sq(Position p) => $"{(char)('a' + p.X)}{p.Y + 1}";

    private static PromotionType MapPromotion(string? p) => p?.ToLowerInvariant() switch
    {
        "r" => PromotionType.ToRook,
        "b" => PromotionType.ToBishop,
        "n" => PromotionType.ToKnight,
        _ => PromotionType.ToQueen
    };
}
