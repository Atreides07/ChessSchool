using System.IO.Compression;
using System.Text;
using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

/// <summary>
/// Разбор жеребьёвки из chess-results. Данные синтетические (никаких реальных имён игроков —
/// приватность). Проверяем ядро <see cref="ChessResultsParser.ParseRows"/> и чтение .xlsx целиком.
/// </summary>
public class ChessResultsParserTests
{
    // Сетка как в выгрузке «Пары/Результаты»: сноска, название, метка обновления, затем туры с шапкой и досками.
    private static string?[][] SampleGrid() =>
    [
        ["Из турнирной базы данных Chess-Results https://chess-results.com"],
        ["My Test Cup 2026"],
        ["Последнее обновление 01.01.2026 08:00:00"],
        ["1. Тур on 2026/06/18 в 10:00"],
        ["Bo.", "Ном.", "Рейт", "", "White", "Результат", "", "Black", "Рейт", "Ном."],
        ["1", "1", "1500", "", "Alice", "1 - 0", "", "Bob", "1200", "4"],
        ["2", "2", "1400", "", "Carol", "½ - ½", "", "Dave", "1300", "3"],
        ["2. Тур on 2026/06/18 в 10:15"],
        ["Bo.", "Ном.", "Рейт", "", "White", "Результат", "", "Black", "Рейт", "Ном."],
        ["1", "1", "1500", "", "Alice", "", "", "Carol", "1400", "2"],
        ["2", "4", "1200", "", "Bob", "", "", "Dave", "1300", "3"],
        ["Все подробности на https://chess-results.com/tnr1.aspx"],
    ];

    [Fact]
    public void ParseRows_ExtractsTitlePlayersRounds()
    {
        var doc = ChessResultsParser.ParseRows(SampleGrid());

        Assert.Equal("My Test Cup 2026", doc.Title);
        Assert.Equal(2, doc.Rounds.Count);
        Assert.Equal(4, doc.Players.Count);

        var alice = doc.Players.Single(p => p.No == 1);
        Assert.Equal("Alice", alice.Name);
        Assert.Equal(1500, alice.Rating);
    }

    [Fact]
    public void ParseRows_ParsesColorsAndResults()
    {
        var doc = ChessResultsParser.ParseRows(SampleGrid());
        var r1 = doc.Rounds[0];

        Assert.Equal(2, r1.Boards.Count);
        Assert.Equal(1, r1.Boards[0].WhiteNo);
        Assert.Equal(4, r1.Boards[0].BlackNo);
        Assert.Equal("1-0", r1.Boards[0].Result);
        Assert.Equal("½-½", r1.Boards[1].Result);

        // Второй тур ещё не сыгран — результаты пустые, пары другие.
        Assert.Equal("", doc.Rounds[1].Boards[0].Result);
        Assert.Equal(1, doc.Rounds[1].Boards[0].WhiteNo);
        Assert.Equal(2, doc.Rounds[1].Boards[0].BlackNo);
    }

    [Fact]
    public void ParseRows_HandlesByeWhenOneSideEmpty()
    {
        string?[][] grid =
        [
            ["Bye Test"],
            ["1. Тур"],
            ["Bo.", "Ном.", "Рейт", "", "White", "Результат", "", "Black", "Рейт", "Ном."],
            ["1", "1", "1500", "", "Alice", "1 - 0", "", "Bob", "1200", "2"],
            ["2", "5", "1100", "", "Eve", "", "", "", "", ""],
        ];
        var doc = ChessResultsParser.ParseRows(grid);
        var bye = doc.Rounds[0].Boards[1];

        Assert.Equal(5, bye.WhiteNo);
        Assert.Null(bye.BlackNo); // бай — соперника нет
        Assert.Equal(3, doc.Players.Count); // Alice, Bob, Eve
    }

    [Fact]
    public void ParseRows_ThrowsWhenNoRounds()
        => Assert.Throws<PairingParseException>(() => ChessResultsParser.ParseRows(
            [["Просто заголовок"], ["ещё строка"]]));

    [Theory]
    [InlineData("1 - 0", "1-0")]
    [InlineData("0 - 1", "0-1")]
    [InlineData("½ - ½", "½-½")]
    [InlineData("0.5-0.5", "½-½")]
    [InlineData("", "")]
    [InlineData("+ - -", "+/-")]
    [InlineData("- - +", "-/+")]
    public void NormalizeResult_MapsTokens(string raw, string expected)
        => Assert.Equal(expected, ChessResultsParser.NormalizeResult(raw));

    [Fact]
    public void ParseXlsx_ReadsRealOpenXmlPackage()
    {
        using var xlsx = BuildXlsx(SampleGrid());
        var doc = ChessResultsParser.ParseXlsx(xlsx, "https://chess-results.com/tnr1.aspx");

        Assert.Equal("My Test Cup 2026", doc.Title);
        Assert.Equal("https://chess-results.com/tnr1.aspx", doc.SourceUrl);
        Assert.Equal(2, doc.Rounds.Count);
        Assert.Equal("1-0", doc.Rounds[0].Boards[0].Result);
        Assert.Equal(4, doc.Players.Count);
    }

    [Fact]
    public void ParseHtml_ReadsPairingTable()
    {
        // Таблица как в HTML chess-results: две колонки «Name» (белые/чёрные) и «Result» между ними.
        const string html = """
            <h2>1. Round on 2026/06/18</h2>
            <table>
              <tr><th>Bo.</th><th>No.</th><th>Rtg</th><th>Name</th><th>Result</th><th>Name</th><th>Rtg</th><th>No.</th></tr>
              <tr><td>1</td><td>1</td><td>1500</td><td>Alice</td><td>1 - 0</td><td>Bob</td><td>1200</td><td>2</td></tr>
            </table>
            """;
        var doc = ChessResultsParser.ParseHtml(html);
        Assert.Single(doc.Rounds);
        Assert.Equal(1, doc.Rounds[0].Boards[0].WhiteNo);
        Assert.Equal(2, doc.Rounds[0].Boards[0].BlackNo);
        Assert.Equal("1-0", doc.Rounds[0].Boards[0].Result);
    }

    // ---------- Сборка минимального .xlsx (OpenXML zip) для теста чтения ----------

    private static MemoryStream BuildXlsx(string?[][] rows)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            w.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            for (int r = 0; r < rows.Length; r++)
            {
                w.Write($"<row r=\"{r + 1}\">");
                for (int c = 0; c < rows[r].Length; c++)
                {
                    var v = rows[r][c];
                    if (string.IsNullOrEmpty(v)) continue;
                    // inlineStr — чтобы не вести таблицу sharedStrings (парсер читает оба варианта).
                    w.Write($"<c r=\"{Col(c)}{r + 1}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(v)}</t></is></c>");
                }
                w.Write("</row>");
            }
            w.Write("</sheetData></worksheet>");
        }
        ms.Position = 0;
        return ms;
    }

    private static string Col(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0) { index--; sb.Insert(0, (char)('A' + index % 26)); index /= 26; }
        return sb.ToString();
    }

    private static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
