namespace ChessSchool.Contracts;

/// <summary>
/// Фигуры доски — классический набор Cburnett (стандартные SVG, как на Wikipedia/lichess),
/// отдаются статикой из RCL ChessSchool.Design. Возвращает &lt;img&gt; на нужный файл.
/// </summary>
public static class ChessPieces
{
    public static string Svg(char piece)
    {
        var type = char.ToUpperInvariant(piece);
        if ("KQRBNP".IndexOf(type) < 0) return "";
        var code = (char.IsUpper(piece) ? "w" : "b") + type; // напр. wN, bК
        return $"<img class=\"cp\" alt=\"\" draggable=\"false\" src=\"_content/ChessSchool.Design/pieces/{code}.svg\">";
    }
}
