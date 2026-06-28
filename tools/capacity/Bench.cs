using System.Diagnostics;
using Chess;

namespace ChessSchool.Capacity;

/// <summary>
/// Меряет на этой машине удельные стоимости, которые двигают железо: пропускную способность обработки
/// ходов (на ядро) и память на активную партию. Использует Gera.Chess — ту же библиотеку, что GameServer.
/// Это floor: реальный путь грейна (проверка одного хода) дешевле полной генерации всех ходов.
/// </summary>
public static class Bench
{
    /// <summary>Ходов в секунду на одном ядре: полная генерация легальных ходов + применение.</summary>
    public static double MovesPerSecPerCore(double warmupSec = 0.4, double measureSec = 3.0)
    {
        RunMoves(warmupSec); // прогрев JIT/кэшей
        var (plies, sec) = RunMoves(measureSec);
        return sec > 0 ? plies / sec : 0;
    }

    private static (long Plies, double Sec) RunMoves(double seconds)
    {
        var sw = Stopwatch.StartNew();
        long plies = 0;
        int pick = 0;
        var board = new ChessBoard();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var moves = board.Moves(false); // все легальные ходы (без SAN — как горячий путь)
            if (board.IsEndGame || moves.Length == 0) { board = new ChessBoard(); pick = 0; continue; }
            board.Move(moves[pick++ % moves.Length]);
            plies++;
        }
        sw.Stop();
        return (plies, sw.Elapsed.TotalSeconds);
    }

    /// <summary>Память на партию (байт): доска + история ходов в середине партии (без грейна Orleans).</summary>
    public static int BytesPerGameBoard(int games = 4000, int plies = 40)
    {
        var sink = new ChessBoard[games];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        for (int g = 0; g < games; g++)
        {
            var b = new ChessBoard();
            int pick = 0;
            for (int p = 0; p < plies && !b.IsEndGame; p++)
            {
                var moves = b.Moves(false);
                if (moves.Length == 0) break;
                b.Move(moves[pick++ % moves.Length]);
            }
            sink[g] = b;
        }
        long after = GC.GetTotalMemory(true);
        GC.KeepAlive(sink);
        return (int)((after - before) / games);
    }
}
