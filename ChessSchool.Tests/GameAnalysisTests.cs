using ChessSchool.Arena.Services;
using ChessSchool.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessSchool.Tests;

/// <summary>
/// Разбор партии: парсинг оценки из UCI, воспроизведение PGN и классификация ходов/точность
/// (с подставным оценщиком — без реального Stockfish, чтобы тест был детерминированным и быстрым).
/// </summary>
public class GameAnalysisTests
{
    [Theory]
    [InlineData("info depth 12 score cp 34 nodes 1000 pv e2e4", 34, null)]
    [InlineData("info depth 20 score cp -150 time 5", -150, null)]
    [InlineData("info depth 9 score mate 3 pv f3f7", null, 3)]
    [InlineData("info depth 9 score mate -2", null, -2)]
    [InlineData("info string no score here", null, null)]
    public void ParseScore_ExtractsCpOrMate(string line, int? cp, int? mate)
    {
        var (c, m) = StockfishEngine.ParseScore(line);
        Assert.Equal(cp, c);
        Assert.Equal(mate, m);
    }

    [Fact]
    public void Replay_FromPgn_ReconstructsAllPlies()
    {
        // 1.e4 e5 2.Nf3 — 3 полухода.
        var (start, plies) = GameReplay.FromPgn("1. e4 e5 2. Nf3 *");
        Assert.Equal("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", start);
        Assert.Equal(3, plies.Count);
        Assert.Equal(PieceColor.White, plies[0].Side);
        Assert.Equal(PieceColor.Black, plies[1].Side);
        Assert.Equal("e4", plies[0].San);
        Assert.Equal("e2", plies[0].From);
        Assert.Equal("e4", plies[0].To);
    }

    // Подставной оценщик: задаём оценки В ПОЛЬЗУ БЕЛЫХ (сантипешки) по порядку позиций; возвращаем их
    // в конвенции UCI (со стороны игрока, чей ход) — сервис конвертирует обратно к белым.
    private sealed class FakeEvaluator(int[] whiteCpByPosition) : IPositionEvaluator
    {
        private int _i;
        public Task<EngineEval?> EvaluateAsync(string fen, int moveTimeMs, CancellationToken ct = default)
        {
            int whiteCp = _i < whiteCpByPosition.Length ? whiteCpByPosition[_i] : 0;
            _i++;
            bool whiteToMove = fen.Split(' ')[1] == "w";
            int stm = whiteToMove ? whiteCp : -whiteCp;
            return Task.FromResult<EngineEval?>(new EngineEval(stm, null, null));
        }
    }

    private static GameAnalysisService Svc(IPositionEvaluator eval)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Analysis:MoveTimeMs"] = "1",
        }).Build();
        return new GameAnalysisService(eval, cfg, NullLogger<GameAnalysisService>.Instance);
    }

    [Fact]
    public async Task Analyze_DetectsBlackBlunder_AndScoresAccuracy()
    {
        // Позиции: [перед e4, после e4 (перед ходом чёрных), после хода чёрных].
        // Оценка белых: +20 → +25 → +400 ⇒ ход чёрных резко ухудшил их позицию = зевок.
        var eval = new FakeEvaluator([20, 25, 400]);
        var result = await Svc(eval).AnalyzeAsync("1. e4 e5 *", CancellationToken.None);

        Assert.True(result.EngineAvailable);
        Assert.Equal(2, result.Moves.Count);
        Assert.Equal(MoveQuality.Blunder, result.Moves[1].Quality); // ход чёрных
        Assert.Equal(1, result.BlackBlunders);
        Assert.Equal(0, result.WhiteBlunders);
        Assert.True(result.WhiteAccuracy > result.BlackAccuracy);
    }

    [Fact]
    public async Task Analyze_EngineUnavailable_FlaggedNotAvailable()
    {
        var result = await Svc(new NullEvaluator()).AnalyzeAsync("1. e4 e5 *", CancellationToken.None);
        Assert.False(result.EngineAvailable);
        Assert.Empty(result.Moves);
    }

    private sealed class NullEvaluator : IPositionEvaluator
    {
        public Task<EngineEval?> EvaluateAsync(string fen, int moveTimeMs, CancellationToken ct = default)
            => Task.FromResult<EngineEval?>(null);
    }
}
