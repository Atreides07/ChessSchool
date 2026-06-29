using System.Text.RegularExpressions;

namespace ChessSchool.Arena.Services;

/// <summary>Полуход доски трансляции: нотация, FEN после хода и клетки хода (для рисовки/навигации в браузере).</summary>
public sealed record BroadcastPly(string San, string Fen, string From, string To);

/// <summary>
/// Одна доска (партия) трансляции: участники, результат и позиции по полуходам. Текущая позиция —
/// последний полуход (или стартовая, если ходов нет). Без Orleans-сериализации — отдаётся как JSON.
/// </summary>
public sealed record BroadcastBoard(
    int Board,
    string White,
    string Black,
    string WhiteElo,
    string BlackElo,
    string Result,
    string StartFen,
    IReadOnlyList<BroadcastPly> Plies)
{
    public string Fen => Plies.Count > 0 ? Plies[^1].Fen : StartFen;
    public string? LastFrom => Plies.Count > 0 ? Plies[^1].From : null;
    public string? LastTo => Plies.Count > 0 ? Plies[^1].To : null;
    public int PlyCount => Plies.Count;
    public bool Finished => Result is "1-0" or "0-1" or "1/2-1/2";
}

/// <summary>
/// Разбор «живого» мульти-партийного PGN трансляции (формат lichess/DGT-фидов) в доски с позициями.
/// Чистые функции без сети/состояния — тестируются изолированно. Реплей одной партии переиспользует
/// <see cref="GameReplay.FromPgn"/> (Gera.Chess): SAN → FEN по каждому полуходу.
/// </summary>
public static class BroadcastPgn
{
    private static readonly Regex GameStart = new(@"(?=^\[Event\s)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Header = new("""^\[(\w+)\s+"([^"]*)"\]""", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Comment = new(@"\{[^}]*\}", RegexOptions.Compiled);     // {...} вкл. [%clk]/[%eval]
    private static readonly Regex LineComment = new(@";[^\n]*", RegexOptions.Compiled);   // ; ...
    private static readonly Regex Nag = new(@"\$\d+", RegexOptions.Compiled);             // $1, $2 …

    /// <summary>Разбить фид на отдельные партии по тегу <c>[Event …]</c> в начале строки.</summary>
    public static IReadOnlyList<string> SplitGames(string pgn)
    {
        if (string.IsNullOrWhiteSpace(pgn)) return [];
        var text = pgn.Replace("\r\n", "\n").Replace("\r", "\n");
        var starts = GameStart.Matches(text);
        if (starts.Count == 0) return [text.Trim()]; // без тегов — считаем одной партией

        var games = new List<string>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
        {
            var from = starts[i].Index;
            var to = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
            var chunk = text[from..to].Trim();
            if (chunk.Length > 0) games.Add(chunk);
        }
        return games;
    }

    /// <summary>
    /// Разобрать фид в доски. Партии без участников пропускаются; номер доски берётся из тега
    /// <c>[Board "n"]</c>, иначе по порядку. Доски возвращаются отсортированными по номеру.
    /// </summary>
    public static IReadOnlyList<BroadcastBoard> Parse(string pgn, int maxBoards = 200)
    {
        var boards = new List<BroadcastBoard>();
        int seq = 0;
        foreach (var game in SplitGames(pgn))
        {
            if (boards.Count >= maxBoards) break;
            seq++;

            var headers = ParseHeaders(game);
            var white = headers.GetValueOrDefault("White", "");
            var black = headers.GetValueOrDefault("Black", "");
            if (string.IsNullOrWhiteSpace(white) && string.IsNullOrWhiteSpace(black)) continue;

            var boardNo = int.TryParse(headers.GetValueOrDefault("Board"), out var bn) ? bn : seq;
            var (startFen, plies) = GameReplay.FromPgn(Sanitize(game));

            boards.Add(new BroadcastBoard(
                boardNo,
                string.IsNullOrWhiteSpace(white) ? "?" : white,
                string.IsNullOrWhiteSpace(black) ? "?" : black,
                headers.GetValueOrDefault("WhiteElo", ""),
                headers.GetValueOrDefault("BlackElo", ""),
                headers.GetValueOrDefault("Result", "*"),
                startFen,
                plies.Select(p => new BroadcastPly(p.San, p.Fen, p.From, p.To)).ToList()));
        }
        return boards.OrderBy(b => b.Board).ToList();
    }

    /// <summary>Теги партии (<c>[Key "Value"]</c>) в словарь. Дубликаты — последний выигрывает.</summary>
    public static IReadOnlyDictionary<string, string> ParseHeaders(string game)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Header.Matches(game))
            map[m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    /// <summary>Убрать комментарии и NAG-аннотации из движущегося текста — иначе движок спотыкается о [%clk]/[%eval].</summary>
    private static string Sanitize(string game)
    {
        var s = Comment.Replace(game, " ");
        s = LineComment.Replace(s, " ");
        s = Nag.Replace(s, " ");
        return s;
    }
}
