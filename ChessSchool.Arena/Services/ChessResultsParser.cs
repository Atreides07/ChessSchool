using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ChessSchool.Arena.Services;

// ---------------------- Модель жеребьёвки (отдаётся тонкому клиенту как JSON) ----------------------

/// <summary>Разобранная жеребьёвка турнира из chess-results: стартлист игроков + туры с парами.</summary>
public sealed record PairingDocument(
    string Title,
    string? SourceUrl,
    IReadOnlyList<PairingPlayer> Players,
    IReadOnlyList<PairingRound> Rounds);

/// <summary>Игрок турнира. <see cref="No"/> — стартовый номер (идентичность игрока в турнире).</summary>
public sealed record PairingPlayer(int No, string Name, int? Rating);

/// <summary>Тур: номер, расписание (как в исходнике, напр. «on 2026/06/18 в 10:00») и доски.</summary>
public sealed record PairingRound(int Number, string? Schedule, IReadOnlyList<PairingBoard> Boards);

/// <summary>Доска: пара по стартовым номерам. Пустой <see cref="BlackNo"/> (или White) = бай/без соперника.
/// <see cref="Result"/> нормализован: «1-0» / «0-1» / «½-½» / форфейт (с «+») / «» (не сыграно).</summary>
public sealed record PairingBoard(int Board, int? WhiteNo, int? BlackNo, string Result);

/// <summary>Ошибка разбора жеребьёвки (пустой/непонятный источник) — для дружелюбного ответа клиенту.</summary>
public sealed class PairingParseException(string message) : Exception(message);

// ---------------------- Парсер ----------------------

/// <summary>
/// Разбор выгрузки «Пары/Результаты» из chess-results — из .xlsx (упаковка OpenXML, читаем без сторонних
/// библиотек) и из HTML страницы турнира. Обе ветки сводятся к <see cref="ParseRows"/>: ядро не зависит от
/// формата (таблица строк-ячеек), привязывается к столбцам по заголовкам (White/Black/Name + Результат/Result)
/// и потому переживает разный порядок колонок и язык выгрузки. Чистый и тестируемый.
/// </summary>
public static class ChessResultsParser
{
    // Синонимы заголовков по языкам chess-results (RU/EN/DE) — привязка к колонкам по смыслу, не по позиции.
    private static readonly string[] WhiteHdr = ["white", "белые"];
    private static readonly string[] BlackHdr = ["black", "чёрные", "черные"];
    private static readonly string[] NameHdr = ["name", "имя", "spieler", "участник", "фамилия, имя"];
    private static readonly string[] ResultHdr = ["результат", "result", "res.", "res", "erg.", "ergebnis", "score"];
    private static readonly string[] NoHdr = ["ном.", "ном", "no.", "no", "sno", "snr", "nr.", "nr", "стартовый номер", "№"];
    private static readonly string[] RatingHdr = ["рейт", "rtg", "rating", "рейтинг", "elo"];
    private static readonly string[] BoardHdr = ["bo.", "bo", "br.", "доска", "стол"];

    // Заголовок тура: «1. Тур on 2026/06/18 в 10:00», «Round 1 ...», «1. Runde ...», «Ронда 1 ...».
    private static readonly Regex RoundRe = new(
        @"^\s*(?:(\d{1,3})\s*\.\s*)?(?:тур|round|runde|ronde|раунд|ронда)\b\s*(?:(\d{1,3})\b)?\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Строки-сноски выгрузки, не являющиеся ни заголовком, ни данными.
    private static readonly string[] Boilerplate =
    [
        "chess-results", "chess-tournament-results", "последнее обновление", "last update",
        "из турнирной базы", "all details", "все подробности", "пары/результаты", "pairings",
    ];

    /// <summary>Разбор .xlsx (OpenXML zip): достаём sharedStrings + первый лист, собираем матрицу ячеек.</summary>
    public static PairingDocument ParseXlsx(Stream xlsx, string? sourceUrl = null)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read, leaveOpen: true);

        var shared = ReadSharedStrings(zip);
        var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml")
            ?? zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                               && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new PairingParseException("В файле не найден лист данных.");

        var rows = ReadSheetRows(sheetEntry, shared);
        return ParseRows(rows, sourceUrl);
    }

    /// <summary>Разбор HTML страницы chess-results: таблицы → строки ячеек, заголовки туров — отдельными строками.</summary>
    public static PairingDocument ParseHtml(string html, string? sourceUrl = null)
        => ParseRows(HtmlToRows(html), sourceUrl);

    // ---------------------- Ядро: матрица строк → модель ----------------------

    /// <summary>
    /// Привязка к колонкам по заголовку (повторяется перед каждым туром), затем разбор строк-досок до
    /// следующего заголовка/тура. Имя игрока — по колонке White/Black (или двум колонкам «Name»: первая —
    /// белые, вторая — чёрные); номер/рейтинг — по ближайшим колонкам «Ном.»/«Рейт» от имени.
    /// </summary>
    public static PairingDocument ParseRows(IReadOnlyList<string?[]> rows, string? sourceUrl = null)
    {
        string title = DetectTitle(rows);
        var players = new Dictionary<int, PairingPlayer>();
        var rounds = new List<PairingRound>();

        ColumnMap? cols = null;
        List<PairingBoard>? boards = null;
        int roundNo = 0;
        string? schedule = null;

        void Flush()
        {
            if (boards is { Count: > 0 }) rounds.Add(new PairingRound(roundNo, schedule, boards));
            boards = null;
        }

        foreach (var row in rows)
        {
            var first = Cell(row, 0);

            // Заголовок тура.
            if (TryRound(row, out int n, out string? sched))
            {
                Flush();
                roundNo = n > 0 ? n : roundNo + 1;
                schedule = sched;
                boards = new List<PairingBoard>();
                cols = null; // заголовок колонок придёт ниже
                continue;
            }

            // Заголовок колонок (содержит имена-якоря White/Black или две «Name»).
            if (TryColumns(row, out var map))
            {
                cols = map;
                if (boards is null && rounds.Count == 0 && roundNo == 0)
                {
                    // Колонки до явного заголовка тура (некоторые выгрузки одного тура) — открыть тур 1.
                    roundNo = 1;
                    boards = new List<PairingBoard>();
                }
                continue;
            }

            // Строка-доска.
            if (cols is { } c && boards is not null && TryBoard(row, c, players, out var board))
                boards.Add(board);
        }
        Flush();

        if (rounds.Count == 0)
            throw new PairingParseException("Не удалось распознать жеребьёвку: нет ни одного тура с парами.");

        var orderedPlayers = players.Values.OrderBy(p => p.No).ToList();
        return new PairingDocument(title, sourceUrl, orderedPlayers, rounds);
    }

    private static bool TryRound(string?[] row, out int number, out string? schedule)
    {
        number = 0; schedule = null;
        // Заголовок тура — это «текстовая» строка (данные есть только в первой содержательной ячейке).
        var text = FirstText(row);
        if (text is null) return false;
        var m = RoundRe.Match(text);
        if (!m.Success) return false;
        var num = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        if (int.TryParse(num, out var n)) number = n;
        var tail = m.Groups[3].Value.Trim();
        schedule = string.IsNullOrWhiteSpace(tail) ? null : tail;
        return true;
    }

    private static bool TryColumns(string?[] row, out ColumnMap map)
    {
        map = default!;
        var names = new List<int>();
        int white = -1, black = -1, result = -1, board = -1;
        var nos = new List<int>();
        var ratings = new List<int>();

        for (int i = 0; i < row.Length; i++)
        {
            var h = Norm(row[i]);
            if (h.Length == 0) continue;
            if (Match(h, WhiteHdr)) white = i;
            else if (Match(h, BlackHdr)) black = i;
            else if (Match(h, NameHdr)) names.Add(i);
            else if (Match(h, ResultHdr)) result = i;
            else if (Match(h, BoardHdr)) board = i;
            else if (Match(h, NoHdr)) nos.Add(i);
            else if (Match(h, RatingHdr)) ratings.Add(i);
        }

        // Колонки белых/чёрных: явные White/Black, либо две колонки «Name» (первая=белые, вторая=чёрные).
        if (white < 0 && black < 0 && names.Count >= 2) { white = names[0]; black = names[1]; }
        if (white < 0 || black < 0 || result < 0) return false;

        int whiteNo = NearestBelow(nos, white), blackNo = NearestAbove(nos, black);
        int whiteRtg = NearestBelow(ratings, white), blackRtg = NearestAbove(ratings, black);
        map = new ColumnMap(board, white, black, result, whiteNo, blackNo, whiteRtg, blackRtg);
        return true;
    }

    private static bool TryBoard(string?[] row, ColumnMap c, Dictionary<int, PairingPlayer> players, out PairingBoard board)
    {
        board = default!;
        var whiteName = Cell(row, c.White);
        var blackName = Cell(row, c.Black);
        if (whiteName.Length == 0 && blackName.Length == 0) return false; // не строка-доска

        int? boardNo = c.Board >= 0 ? ToInt(Cell(row, c.Board)) : null;
        int? whiteNo = Register(players, ToInt(Cell(row, c.WhiteNo)), whiteName, ToInt(Cell(row, c.WhiteRtg)));
        int? blackNo = Register(players, ToInt(Cell(row, c.BlackNo)), blackName, ToInt(Cell(row, c.BlackRtg)));
        if (whiteNo is null && blackNo is null) return false;

        var result = NormalizeResult(Cell(row, c.Result));
        board = new PairingBoard(boardNo ?? 0, whiteNo, blackNo, result);
        return true;
    }

    // Регистрирует игрока (по номеру) и возвращает его номер; пустое имя/номер → null (нет игрока на стороне).
    private static int? Register(Dictionary<int, PairingPlayer> players, int? no, string name, int? rating)
    {
        name = name.Trim();
        if (no is null || name.Length == 0 || IsByeName(name)) return null;
        if (!players.TryGetValue(no.Value, out var p) || string.IsNullOrEmpty(p.Name))
            players[no.Value] = new PairingPlayer(no.Value, name, rating);
        else if (rating is not null && p.Rating is null)
            players[no.Value] = p with { Rating = rating };
        return no;
    }

    private static bool IsByeName(string name)
    {
        var n = name.ToLowerInvariant();
        return n is "bye" or "spielfrei" or "(bye)" or "пусто" or "без игры" || n.Contains("not paired");
    }

    /// <summary>Нормализация результата к компактным токенам, понятным редактору/экспорту.</summary>
    public static string NormalizeResult(string raw)
    {
        var s = raw.Replace(" ", "").Replace("–", "-").Replace("—", "-").Trim();
        if (s.Length == 0 || s is "-" or "*") return "";
        if (s.Contains('½') || s is "0.5-0.5" or "0,5-0,5" || s.Contains("½-½")) return "½-½";
        // Форфейты chess-results: «+--», «--+», «1-0F», «0-1F», «1F-0», «+/-» и т.п. — сохраняем как форфейт.
        bool forfeit = s.Contains('+') || s.Contains('F') || s.Contains('f');
        bool whiteSide = s.StartsWith('1') || s.StartsWith('+');
        bool blackSide = s.StartsWith('0') || s.StartsWith('-');
        if (forfeit)
        {
            if (s.StartsWith('+') || s.Contains("1-0") || s.StartsWith("1")) return "+/-";
            if (s.Contains("0-1") || s.StartsWith("0") || s.StartsWith("-")) return "-/+";
            return "+/-";
        }
        if (s.StartsWith("1-0")) return "1-0";
        if (s.StartsWith("0-1")) return "0-1";
        // Бай-очко в одиночной ячейке: «1» / «½» / «0».
        if (s == "1") return "1-0";
        return whiteSide && !blackSide ? "1-0" : blackSide && !whiteSide ? "0-1" : "";
    }

    private static string DetectTitle(IReadOnlyList<string?[]> rows)
    {
        foreach (var row in rows.Take(8))
        {
            var t = FirstText(row);
            if (t is null) continue;
            var low = t.ToLowerInvariant();
            if (Boilerplate.Any(b => low.Contains(b))) continue;
            if (RoundRe.IsMatch(t)) continue;
            if (t.Trim().Length >= 3) return t.Trim();
        }
        return "Жеребьёвка";
    }

    // ---------------------- Чтение xlsx (OpenXML) ----------------------

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return list;
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        foreach (var si in doc.Root!.Elements(S + "si"))
            list.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));
        return list;
    }

    private static List<string?[]> ReadSheetRows(ZipArchiveEntry sheet, List<string> shared)
    {
        using var stream = sheet.Open();
        var doc = XDocument.Load(stream);
        var result = new List<string?[]>();
        foreach (var r in doc.Descendants(S + "row"))
        {
            var cells = new List<(int Col, string Val)>();
            int maxCol = 0;
            foreach (var c in r.Elements(S + "c"))
            {
                int col = ColIndex((string?)c.Attribute("r"));
                string val = CellValue(c, shared);
                if (val.Length > 0) cells.Add((col, val));
                if (col + 1 > maxCol) maxCol = col + 1;
            }
            var arr = new string?[maxCol];
            foreach (var (col, val) in cells) if (col < maxCol) arr[col] = val;
            result.Add(arr);
        }
        return result;
    }

    private static string CellValue(XElement c, List<string> shared)
    {
        var t = (string?)c.Attribute("t");
        if (t == "s") // shared string
        {
            var idx = c.Element(S + "v")?.Value;
            return int.TryParse(idx, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
        }
        if (t == "inlineStr")
            return string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(x => x.Value) ?? []);
        return c.Element(S + "v")?.Value ?? "";
    }

    private static int ColIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return 0;
        int n = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') n = n * 26 + (ch - 'a' + 1);
            else break;
        }
        return Math.Max(0, n - 1);
    }

    // ---------------------- Чтение HTML (таблицы + заголовки туров, порядок документа) ----------------------

    private static readonly Regex BlockRe = new(
        @"<tr\b[^>]*>(?<tr>.*?)</tr>|<(?:h[1-6]|caption|b)\b[^>]*>(?<h>.*?)</(?:h[1-6]|caption|b)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CellRe = new(@"<t[dh]\b[^>]*>(?<c>.*?)</t[dh]>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRe = new("<[^>]+>", RegexOptions.Compiled);

    private static List<string?[]> HtmlToRows(string html)
    {
        var rows = new List<string?[]>();
        foreach (Match m in BlockRe.Matches(html))
        {
            if (m.Groups["tr"].Success)
            {
                var cells = new List<string?>();
                foreach (Match cm in CellRe.Matches(m.Groups["tr"].Value))
                    cells.Add(StripHtml(cm.Groups["c"].Value));
                if (cells.Count > 0) rows.Add(cells.ToArray());
            }
            else // заголовок/жирный текст — кандидат на заголовок тура
            {
                var text = StripHtml(m.Groups["h"].Value);
                if (text.Length > 0) rows.Add([text]);
            }
        }
        return rows;
    }

    private static readonly Regex WsRe = new(@"\s+", RegexOptions.Compiled);

    private static string StripHtml(string s)
    {
        var noTags = WsRe.Replace(TagRe.Replace(s, " "), " ");
        return System.Net.WebUtility.HtmlDecode(noTags).Replace(' ', ' ').Trim();
    }

    // ---------------------- Мелкие помощники ----------------------

    private readonly record struct ColumnMap(
        int Board, int White, int Black, int Result, int WhiteNo, int BlackNo, int WhiteRtg, int BlackRtg);

    private static string Cell(string?[] row, int i) => i >= 0 && i < row.Length ? (row[i] ?? "").Trim() : "";

    private static string? FirstText(string?[] row)
    {
        // «Текстовая» строка-заголовок: ровно одна непустая ячейка со значимым текстом.
        var nonEmpty = row.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).ToList();
        return nonEmpty.Count >= 1 ? nonEmpty[0] : null;
    }

    private static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();

    private static bool Match(string header, string[] synonyms)
        => synonyms.Any(syn => header == syn || header.StartsWith(syn + " ") || header == syn + ".");

    private static int? ToInt(string s)
    {
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var n) ? n : null;
    }

    private static int NearestBelow(List<int> idxs, int anchor)
    {
        int best = -1;
        foreach (var i in idxs) if (i < anchor && i > best) best = i;
        return best;
    }

    private static int NearestAbove(List<int> idxs, int anchor)
    {
        int best = -1;
        foreach (var i in idxs) if (i > anchor && (best < 0 || i < best)) best = i;
        return best;
    }
}
