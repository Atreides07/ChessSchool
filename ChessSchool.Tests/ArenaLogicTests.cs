using ChessSchool.Arena.Services;
using ChessSchool.Contracts;

namespace ChessSchool.Tests;

/// <summary>Чистая логика арены, вынесенная из грейна: часы, тайминг ботов, ничья, подбор пар.</summary>
public class ArenaClockTests
{
    [Fact]
    public void Deduct_Normal_SubtractsElapsed()
    {
        var (ms, timedOut) = ArenaClock.Deduct(10_000, 1_500);
        Assert.Equal(8_500, ms);
        Assert.False(timedOut);
    }

    [Theory]
    [InlineData(1_000, 1_000)] // ровно в ноль
    [InlineData(500, 1_200)]   // ушёл в минус
    public void Deduct_AtOrBelowZero_FlagsTimeout(long ms, long elapsed)
    {
        var (left, timedOut) = ArenaClock.Deduct(ms, elapsed);
        Assert.Equal(0, left);
        Assert.True(timedOut);
    }

    [Fact]
    public void ResolveTimeout_WinnerHasMatingMaterial_IsWin()
    {
        // Белые просрочили; у чёрных ферзь → чёрные выигрывают по времени.
        var (result, reason) = ArenaClock.ResolveTimeout("q6k/8/8/8/8/8/8/7K w - - 0 1", PieceColor.White);
        Assert.Equal(GameResult.BlackWins, result);
        Assert.Equal(GameEndReason.Timeout, reason);
    }

    [Fact]
    public void ResolveTimeout_OpponentLacksMaterial_IsDraw()
    {
        // Белые просрочили; у чёрных только король → ничья (FIDE 6.9).
        var (result, reason) = ArenaClock.ResolveTimeout("7k/8/8/8/8/8/8/7K w - - 0 1", PieceColor.White);
        Assert.Equal(GameResult.Draw, result);
        Assert.Equal(GameEndReason.InsufficientMaterial, reason);
    }
}

public class ArenaBotTimingTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.999)]
    public void ForcedMove_IsAlmostInstant(double roll)
    {
        var ms = ArenaBotTiming.ThinkMs(1, inCheck: false, myMs: 180_000, speedFactor: 1.0, forcedRoll: roll, jitter: 0.5);
        Assert.InRange(ms, 80, 199);
    }

    [Fact]
    public void Result_IsClampedToBounds()
    {
        var big = ArenaBotTiming.ThinkMs(40, inCheck: true, myMs: long.MaxValue / 2, speedFactor: 5, forcedRoll: 0, jitter: 1);
        Assert.InRange(big, 90, 2500);
        var tiny = ArenaBotTiming.ThinkMs(2, inCheck: false, myMs: 10, speedFactor: 0, forcedRoll: 0, jitter: 0);
        Assert.Equal(90, tiny); // нулевой speedFactor → ниже минимума → клампится в 90
    }

    [Fact]
    public void FasterPersona_ThinksAtLeastAsLong_WhenScaledUp()
    {
        var slow = ArenaBotTiming.ThinkMs(20, false, 60_000, speedFactor: 0.6, forcedRoll: 0, jitter: 0.5);
        var fast = ArenaBotTiming.ThinkMs(20, false, 60_000, speedFactor: 1.4, forcedRoll: 0, jitter: 0.5);
        Assert.True(fast >= slow); // выше множитель — не меньше времени (в пределах клампа)
    }
}

public class ArenaBotDrawTests
{
    [Fact]
    public void BotCp_FlipsSign_WhenOpponentToMove()
    {
        var eval = new EngineEval(Cp: 50, Mate: null, BestMove: null);
        // Ход белых, бот белыми → его ход → +50.
        Assert.Equal(50, ArenaBotDraw.BotCp(eval, botIsWhite: true, whiteToMove: true));
        // Ход белых, бот чёрными → ход соперника → знак инвертируется.
        Assert.Equal(-50, ArenaBotDraw.BotCp(eval, botIsWhite: false, whiteToMove: true));
    }

    [Fact]
    public void BotCp_MateIsHugeMagnitude()
    {
        var mate = new EngineEval(Cp: null, Mate: 2, BestMove: null);
        Assert.Equal(100000, ArenaBotDraw.BotCp(mate, botIsWhite: true, whiteToMove: true));
        var mated = new EngineEval(Cp: null, Mate: -2, BestMove: null);
        Assert.Equal(-100000, ArenaBotDraw.BotCp(mated, botIsWhite: true, whiteToMove: true));
    }

    [Theory]
    [InlineData(-200, 5, true)]  // заметно хуже — всегда согласен
    [InlineData(10, 9, false)]   // равно, но ещё дебют (ход < 10)
    [InlineData(10, 10, true)]   // равно, вне дебюта — согласен
    [InlineData(50, 20, false)]  // лучше — играет дальше
    public void ShouldAccept_AppliesThresholds(int botCp, int fullmove, bool expected)
    {
        Assert.Equal(expected, ArenaBotDraw.ShouldAccept(botCp, fullmove));
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 1)]
    [InlineData("8/8/8/8/8/8/8/8 w - - 0 42", 42)]
    [InlineData("garbage", 1)]
    public void FullmoveFromFen_ReadsField6(string fen, int expected)
    {
        Assert.Equal(expected, ArenaBotDraw.FullmoveFromFen(fen));
    }
}

public class ArenaPairingTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset Waited => Now.AddSeconds(-20); // прождал больше грейса (10с)
    private const int Grace = 10;

    private static SeekingHuman H(string key, DateTimeOffset? since) => new(key, since);

    [Fact]
    public void TwoHumans_PairImmediately_NoBots()
    {
        var plan = ArenaPairing.Plan([H("a", Now), H("b", Now)], [], Now, Grace, botsEnabled: true);
        Assert.Equal(new[] { ("a", "b") }, plan.Pairs);
        Assert.Empty(plan.HumansNeedingNewBot);
    }

    [Fact]
    public void WaitingHuman_GetsIdleBot_AfterGrace()
    {
        var plan = ArenaPairing.Plan([H("a", Waited)], ["bot1"], Now, Grace, botsEnabled: true);
        Assert.Equal(new[] { ("a", "bot1") }, plan.Pairs);
        Assert.Empty(plan.HumansNeedingNewBot);
    }

    [Fact]
    public void WaitingHuman_NoIdleBot_BotsEnabled_RequestsFreshBot()
    {
        var plan = ArenaPairing.Plan([H("a", Waited)], [], Now, Grace, botsEnabled: true);
        Assert.Empty(plan.Pairs);
        Assert.Equal(new[] { "a" }, plan.HumansNeedingNewBot);
    }

    [Fact]
    public void WaitingHuman_NoBot_BotsDisabled_KeepsWaiting()
    {
        var plan = ArenaPairing.Plan([H("a", Waited)], [], Now, Grace, botsEnabled: false);
        Assert.Empty(plan.Pairs);
        Assert.Empty(plan.HumansNeedingNewBot);
    }

    [Fact]
    public void HumanWithinGrace_IsNotPairedWithBot()
    {
        var plan = ArenaPairing.Plan([H("a", Now.AddSeconds(-3))], ["bot1"], Now, Grace, botsEnabled: true);
        Assert.Empty(plan.Pairs);
        Assert.Empty(plan.HumansNeedingNewBot);
    }

    [Fact]
    public void IdleBots_PairAmongThemselves()
    {
        var plan = ArenaPairing.Plan([], ["b1", "b2", "b3", "b4"], Now, Grace, botsEnabled: true);
        Assert.Equal(new[] { ("b1", "b2"), ("b3", "b4") }, plan.Pairs);
    }

    [Fact]
    public void OddHuman_AfterHumanPair_GetsBot()
    {
        // h0+h1 спариваются между собой; h2 (ждущий) получает свободного бота; остаётся 0 ботов на этап 3.
        var plan = ArenaPairing.Plan(
            [H("h0", Waited), H("h1", Waited), H("h2", Waited)], ["bot1"], Now, Grace, botsEnabled: true);
        Assert.Equal(new[] { ("h0", "h1"), ("h2", "bot1") }, plan.Pairs);
        Assert.Empty(plan.HumansNeedingNewBot);
    }
}
