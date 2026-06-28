using Chess;
using ChessSchool.Contracts;
using Color = ChessSchool.Contracts.PieceColor;

namespace ChessSchool.Arena.Services;

/// <summary>Полуход для воспроизведения: номер, нотация, сторона, FEN после хода и клетки хода.</summary>
public sealed record ReplayPly(int Number, string San, Color Side, string Fen, string From, string To);

/// <summary>Воспроизведение партии из PGN: стартовый FEN + список полуходов (для доски/навигации).</summary>
public static class GameReplay
{
    public static (string StartFen, IReadOnlyList<ReplayPly> Plies) FromPgn(string pgn)
    {
        var startFen = new ChessBoard().ToFen();
        var plies = new List<ReplayPly>();
        try
        {
            var src = ChessBoard.LoadFromPgn(pgn);
            var board = new ChessBoard();
            int n = 0;
            foreach (var m in src.ExecutedMoves)
            {
                bool white = board.Turn.AsChar == 'w';
                board.Move(m);
                plies.Add(new ReplayPly(++n, m.San ?? "", white ? Color.White : Color.Black,
                    board.ToFen(), Sq(m.OriginalPosition), Sq(m.NewPosition)));
            }
        }
        catch { /* битый PGN → пустой реплей, страница покажет сообщение */ }
        return (startFen, plies);
    }

    private static string Sq(Position p) => $"{(char)('a' + p.X)}{p.Y + 1}";
}

/// <summary>
/// Оркестратор разбора: история партий и детали — из ApiService; разбор — из кэша, иначе считаем
/// движком и кэшируем (один раз на партию). Гейтинг премиума делает вызывающий (страница/эндпоинт).
/// </summary>
public sealed class ArenaReviewService(
    ArenaGamesApiClient api, GameAnalysisService analysis, ILogger<ArenaReviewService> log)
{
    public Task<ArenaGameListPage> ListAsync(string sub, int skip, int take, CancellationToken ct)
        => api.ListAsync(sub, skip, take, ct);

    public Task<ArenaPlayerStats> GetStatsAsync(string sub, CancellationToken ct)
        => api.GetStatsAsync(sub, ct);

    public Task<ArenaGameDetail?> GetAsync(Guid id, string sub, CancellationToken ct)
        => api.GetAsync(id, sub, ct);

    /// <summary>Только кэш разбора (быстрый путь, без расчёта). null — кэша нет/не участник.</summary>
    public Task<GameAnalysisDto?> GetCachedAnalysisAsync(Guid id, string sub, CancellationToken ct)
        => api.GetCachedAnalysisAsync(id, sub, ct);

    /// <summary>Разбор по уже известному PGN: из кэша, иначе считаем движком и кэшируем. PGN передаём, чтобы
    /// не ходить в ApiService за партией повторно (вызывается из интерактивного контура).</summary>
    public async Task<GameAnalysisDto?> ComputeAnalysisAsync(Guid id, string sub, string pgn, CancellationToken ct)
    {
        var cached = await api.GetCachedAnalysisAsync(id, sub, ct);
        if (cached is not null) return cached;

        var result = await analysis.AnalyzeAsync(pgn, ct);
        if (result.EngineAvailable && result.Moves.Count > 0)
        {
            try { await api.SaveAnalysisAsync(id, result, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Кэш разбора {Id} не сохранён.", id); }
        }
        return result;
    }
}
