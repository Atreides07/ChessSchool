using Chess;
using ChessSchool.Contracts;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Разбор партии движком: воспроизводит PGN, оценивает каждую позицию Stockfish, классифицирует ходы
/// (потеря оценки относительно лучшего) и считает точность сторон (модель win% как на lichess).
/// Тяжёлый расчёт — конкурентность ограничена; результат кэшируется вызывающим (один раз на партию).
/// </summary>
public sealed class GameAnalysisService(
    IPositionEvaluator evaluator, IConfiguration config, ILogger<GameAnalysisService> log)
{
    private readonly int _moveTimeMs = config.GetValue("Analysis:MoveTimeMs", 250);
    private readonly int _maxPlies = config.GetValue("Analysis:MaxPlies", 200);
    private readonly int _maxConcurrent = config.GetValue("Analysis:MaxConcurrent", 2);

    // Ограничиваем число одновременных разборов на ноду (каждый — десятки оценок), чтобы не копить очередь.
    private static SemaphoreSlim? _gate;
    private SemaphoreSlim Gate => _gate ??= new(_maxConcurrent, _maxConcurrent);

    private const int MateCp = 100_000;   // мат → крупная оценка
    private const int ClampCp = 1500;     // для классификации/точности оценку режем (мат не должен «зашкаливать» метрику)

    public async Task<GameAnalysisDto> AnalyzeAsync(string pgn, CancellationToken ct)
    {
        // 1) Воспроизводим партию: FEN перед каждым полуходом + финальный FEN.
        List<string> fens = [];
        List<Move> moves = [];
        try
        {
            var src = ChessBoard.LoadFromPgn(pgn);
            var board = new ChessBoard();
            foreach (var m in src.ExecutedMoves.Take(_maxPlies))
            {
                fens.Add(board.ToFen());
                moves.Add(m);
                board.Move(m);
            }
            fens.Add(board.ToFen()); // позиция после последнего хода
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось разобрать PGN для анализа.");
            return Empty(engineAvailable: true);
        }
        if (moves.Count == 0) return Empty(engineAvailable: true);

        // 2) Оцениваем каждую позицию (с ограничением параллельных разборов на ноду).
        await Gate.WaitAsync(ct);
        EngineEval?[] evals;
        try
        {
            evals = new EngineEval?[fens.Count];
            for (int i = 0; i < fens.Count; i++)
                evals[i] = await evaluator.EvaluateAsync(fens[i], _moveTimeMs, ct);
        }
        finally { Gate.Release(); }

        if (evals.All(e => e is null)) return Empty(engineAvailable: false); // движок недоступен

        // 3) Оценка в сантипешках со стороны белых для каждой позиции.
        var whiteCp = new int[fens.Count];
        for (int i = 0; i < fens.Count; i++)
            whiteCp[i] = ToWhiteCp(evals[i], WhiteToMove(fens[i]));

        // 4) По каждому полуходу: классификация и вклад в точность.
        var analyses = new List<MoveAnalysisDto>(moves.Count);
        int wInacc = 0, wMist = 0, wBlun = 0, bInacc = 0, bMist = 0, bBlun = 0;
        List<double> wAcc = [], bAcc = [];

        for (int i = 0; i < moves.Count; i++)
        {
            bool whiteMoved = WhiteToMove(fens[i]);
            // С точки зрения ходившего: «до» — оценка перед ходом, «после» — после хода.
            int beforeS = Persp(whiteCp[i], whiteMoved);
            int afterS = Persp(whiteCp[i + 1], whiteMoved);

            int cpLoss = Math.Max(0, Clamp(beforeS) - Clamp(afterS));
            var played = Uci(moves[i]);
            var best = evals[i]?.BestMove;
            bool isBest = best is not null && best.StartsWith(played, StringComparison.Ordinal);
            var quality = Classify(cpLoss, isBest);

            // Точность хода по падению win% (модель lichess).
            double winBefore = WinPct(beforeS), winAfter = WinPct(afterS);
            double acc = MoveAccuracy(winBefore, winAfter);
            (whiteMoved ? wAcc : bAcc).Add(acc);

            if (whiteMoved) Tally(quality, ref wInacc, ref wMist, ref wBlun);
            else Tally(quality, ref bInacc, ref bMist, ref bBlun);

            var (mateWhite, scoreWhite) = Display(evals[i + 1], WhiteToMove(fens[i + 1]), whiteCp[i + 1]);
            analyses.Add(new MoveAnalysisDto(
                i + 1, moves[i].San ?? played, whiteMoved ? Color.White : Color.Black,
                scoreWhite, mateWhite, quality,
                best is { Length: >= 4 } ? best[..2] : null,
                best is { Length: >= 4 } ? best[2..4] : null));
        }

        return new GameAnalysisDto(
            Round(Average(wAcc)), Round(Average(bAcc)),
            wInacc, wMist, wBlun, bInacc, bMist, bBlun, analyses, EngineAvailable: true);
    }

    private static GameAnalysisDto Empty(bool engineAvailable) =>
        new(0, 0, 0, 0, 0, 0, 0, 0, [], engineAvailable);

    private static bool WhiteToMove(string fen)
    {
        var parts = fen.Split(' ');
        return parts.Length < 2 || parts[1] == "w";
    }

    private static int ToWhiteCp(EngineEval? e, bool whiteToMove)
    {
        if (e is null) return 0;
        if (e.Value.Mate is int m) { int signed = (m >= 0 ? MateCp : -MateCp) - Math.Sign(m) * Math.Abs(m); return whiteToMove ? signed : -signed; }
        int cp = e.Value.Cp ?? 0;
        return whiteToMove ? cp : -cp;
    }

    // Оценка/мат для отображения (со стороны белых) в позиции ПОСЛЕ хода.
    private static (int? MateWhite, int ScoreCpWhite) Display(EngineEval? e, bool whiteToMove, int whiteCp)
    {
        if (e?.Mate is int m)
        {
            int mateWhite = whiteToMove ? m : -m;
            return (mateWhite, whiteCp);
        }
        return (null, whiteCp);
    }

    private static int Persp(int whiteCp, bool white) => white ? whiteCp : -whiteCp;
    private static int Clamp(int cp) => Math.Clamp(cp, -ClampCp, ClampCp);

    private static MoveQuality Classify(int cpLoss, bool isBest)
    {
        if (isBest) return MoveQuality.Best;
        if (cpLoss < 50) return MoveQuality.Good;
        if (cpLoss < 120) return MoveQuality.Inaccuracy;
        if (cpLoss < 250) return MoveQuality.Mistake;
        return MoveQuality.Blunder;
    }

    private static void Tally(MoveQuality q, ref int inacc, ref int mist, ref int blun)
    {
        if (q == MoveQuality.Inaccuracy) inacc++;
        else if (q == MoveQuality.Mistake) mist++;
        else if (q == MoveQuality.Blunder) blun++;
    }

    // Шанс на победу по оценке (модель lichess): 0..100%.
    private static double WinPct(int cp)
    {
        cp = Math.Clamp(cp, -ClampCp, ClampCp);
        return 50 + 50 * (2 / (1 + Math.Exp(-0.00368208 * cp)) - 1);
    }

    // Точность хода по падению win% (формула lichess), 0..100.
    private static double MoveAccuracy(double winBefore, double winAfter)
    {
        double drop = Math.Max(0, winBefore - winAfter);
        return Math.Clamp(103.1668 * Math.Exp(-0.04354 * drop) - 3.1669, 0, 100);
    }

    private static double Average(List<double> xs) => xs.Count == 0 ? 100 : xs.Average();
    private static double Round(double x) => Math.Round(x, 1);

    private static string Uci(Move m) => $"{Sq(m.OriginalPosition)}{Sq(m.NewPosition)}";
    private static string Sq(Position p) => $"{(char)('a' + p.X)}{p.Y + 1}";
}
