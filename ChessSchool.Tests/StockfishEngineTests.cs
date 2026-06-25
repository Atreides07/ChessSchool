using System.Text.RegularExpressions;
using ChessSchool.Arena.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessSchool.Tests;

/// <summary>
/// Проверяет серверный движок Stockfish. Если бинарь не установлен на машине —
/// тест проходит (graceful fallback: движок возвращает null, бот ходит случайно).
/// </summary>
public class StockfishEngineTests
{
    [Fact]
    public async Task ReturnsLegalUciMove_ForStartPosition_WhenEngineAvailable()
    {
        var config = new ConfigurationBuilder().Build();
        await using var engine = new StockfishEngine(config, NullLogger<StockfishEngine>.Instance);

        const string startFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        var move = await engine.GetBestMoveAsync(startFen, skillLevel: 5, moveTimeMs: 300);

        if (move is null) return; // Stockfish не установлен — это допустимо (fallback на случайный ход)

        // UCI-ход: from+to (+опц. фигура превращения), напр. "e2e4" / "e7e8q".
        Assert.Matches(new Regex("^[a-h][1-8][a-h][1-8][qrbn]?$"), move);
    }
}
