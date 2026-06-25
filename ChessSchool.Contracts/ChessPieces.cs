namespace ChessSchool.Contracts;

/// <summary>
/// SVG-фигуры для доски. Рендерятся векторно (не зависят от шахматных глифов шрифта,
/// которые в части браузеров/шрифтов не отображаются). Цвет берётся из currentColor.
/// </summary>
public static class ChessPieces
{
    public static string Svg(char piece)
    {
        var body = char.ToLowerInvariant(piece) switch
        {
            'p' => Pawn,
            'r' => Rook,
            'n' => Knight,
            'b' => Bishop,
            'q' => Queen,
            'k' => King,
            _ => ""
        };
        if (body.Length == 0) return "";
        return "<svg viewBox=\"0 0 45 45\" class=\"cp\" xmlns=\"http://www.w3.org/2000/svg\" " +
               "fill=\"currentColor\" stroke=\"#111\" stroke-width=\"1.1\" stroke-linejoin=\"round\">" + body + "</svg>";
    }

    private const string Pawn =
        "<path d=\"M22.5 9a4 4 0 0 1 2.6 7.1c2 1.2 3.4 3.4 3.4 6 0 2-1 3.8-2.4 5l2.9 2.4c2.4 2 4 4.2 4 7.5H12c0-3.3 1.6-5.5 4-7.5l2.9-2.4A6 6 0 0 1 16.5 22c0-2.6 1.4-4.8 3.4-6A4 4 0 0 1 22.5 9z\"/>";

    private const string Rook =
        "<path d=\"M12 39v-3l3-2v-9l-2-2v-8h4v3h3v-3h5v3h3v-3h4v8l-2 2v9l3 2v3z\"/>";

    private const string Knight =
        "<path d=\"M18 10c1-1 3-2 5-2 7 0 12 6 12 16v14H13c0-6 3-9 7-12-2 1-5 2-7 1-2-1-2-3-1-5-2 1-4 1-5-1-1-3 1-5 4-7 .5-1 1-2 0-3 1-1 2-1 3 0z\"/>";

    private const string Bishop =
        "<path d=\"M22.5 7a2.5 2.5 0 0 1 2.5 2.5c0 1-.6 1.9-1.4 2.3 3 1.8 5.4 5.2 5.4 9.2 0 3-1.3 5-3 6.5l2 2.5c2 2 3 3.5 3 5.5H11c0-2 1-3.5 3-5.5l2-2.5c-1.7-1.5-3-3.5-3-6.5 0-4 2.4-7.4 5.4-9.2A2.5 2.5 0 0 1 22.5 7z\"/>" +
        "<path d=\"M22.5 12.5v10\" fill=\"none\" stroke=\"#111\" stroke-width=\"1.4\"/>";

    private const string Queen =
        "<path d=\"M12 39v-3h21v3z\"/>" +
        "<path d=\"M13.5 34l-2.5-14 5 6 3-9 3 9 3-6 3 9 3-6-2 11z\"/>" +
        "<circle cx=\"11\" cy=\"17\" r=\"2.5\"/><circle cx=\"22.5\" cy=\"13\" r=\"2.5\"/><circle cx=\"34\" cy=\"17\" r=\"2.5\"/>";

    private const string King =
        "<path d=\"M21 6h3v3h3v3h-3v2.5c4 .5 7 3.5 7 8 0 3-1.3 5-3 6.5l2 2.5c2 2 3 3.5 3 5.5H12c0-2 1-3.5 3-5.5l2-2.5c-1.7-1.5-3-3.5-3-6.5 0-4.5 3-7.5 7-8V12h-3V9h3z\"/>";
}
