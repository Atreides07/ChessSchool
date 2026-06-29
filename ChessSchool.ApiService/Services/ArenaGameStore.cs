using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Domain;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.ApiService.Services;

/// <summary>
/// Архив арена-партий (B2C): запись завершённой партии (идемпотентно), история по игроку (sub),
/// выдача партии для воспроизведения и кэш разбора. Источник истины — Postgres, общий для всех нод.
/// </summary>
public sealed class ArenaGameStore(SchoolDbContext db)
{
    /// <summary>Сохраняет завершённую партию. Повтор по тому же ExternalGameId игнорируется.</summary>
    public async Task<bool> ArchiveAsync(ArenaGameArchiveRequest r, CancellationToken ct)
    {
        if (await db.ArenaGames.AnyAsync(g => g.ExternalGameId == r.GameId, ct))
            return false;

        db.ArenaGames.Add(new ArenaGame
        {
            TournamentId = r.TournamentId,
            ExternalGameId = r.GameId,
            WhiteSub = r.WhiteSub,
            BlackSub = r.BlackSub,
            WhiteName = r.WhiteName,
            BlackName = r.BlackName,
            WhiteIsBot = r.WhiteIsBot,
            BlackIsBot = r.BlackIsBot,
            Pgn = r.Pgn,
            Result = r.Result,
            EndReason = r.EndReason,
            TimeControl = r.TimeControl,
            PlayedAt = r.PlayedAt,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Гонка двух нод на уникальном ExternalGameId — партия уже записана, это не ошибка.
            return false;
        }
    }

    /// <summary>История партий игрока (по sub) от свежих к старым, с пагинацией. Проекция в DTO (без PGN).</summary>
    public async Task<ArenaGameListPage> ListForPlayerAsync(string sub, int skip, int take, CancellationToken ct)
    {
        var q = db.ArenaGames.AsNoTracking()
            .Where(g => g.WhiteSub == sub || g.BlackSub == sub)
            .OrderByDescending(g => g.PlayedAt);

        var total = await q.CountAsync(ct);
        var rows = await q.Skip(skip).Take(take).Select(g => new
        {
            g.Id,
            g.TournamentId,
            g.WhiteSub,
            g.WhiteName,
            g.BlackName,
            g.WhiteIsBot,
            g.BlackIsBot,
            g.Result,
            g.EndReason,
            g.TimeControl,
            g.PlayedAt,
            Analyzed = g.AnalysisJson != null,
        }).ToListAsync(ct);

        var items = rows.Select(g =>
        {
            bool meWhite = g.WhiteSub == sub;
            var outcome = g.Result == GameResult.Draw ? PlayerOutcome.Draw
                : (g.Result == GameResult.WhiteWins) == meWhite ? PlayerOutcome.Win : PlayerOutcome.Loss;
            return new ArenaGameListItem(
                g.Id, g.TournamentId,
                meWhite ? g.BlackName : g.WhiteName,
                meWhite ? g.BlackIsBot : g.WhiteIsBot,
                meWhite ? PieceColor.White : PieceColor.Black,
                outcome, g.EndReason, g.TimeControl, g.PlayedAt, g.Analyzed);
        }).ToList();

        return new ArenaGameListPage(items, total);
    }

    /// <summary>Сводная статистика игрока (для профиля): всего/победы/поражения/ничьи. Одним запросом
    /// с условной агрегацией (WhiteSub/BlackSub под индексом) — один round-trip вместо четырёх Count,
    /// строки в память не тащим. На горячем пути профиля это снимает 4× нагрузку с БД.</summary>
    public async Task<ArenaPlayerStats> GetStatsAsync(string sub, CancellationToken ct)
    {
        var agg = await db.ArenaGames.AsNoTracking()
            .Where(g => g.WhiteSub == sub || g.BlackSub == sub)
            .GroupBy(_ => 1)
            .Select(grp => new
            {
                Total = grp.Count(),
                Wins = grp.Count(g =>
                    (g.Result == GameResult.WhiteWins && g.WhiteSub == sub) ||
                    (g.Result == GameResult.BlackWins && g.BlackSub == sub)),
                Draws = grp.Count(g => g.Result == GameResult.Draw),
                Losses = grp.Count(g =>
                    (g.Result == GameResult.WhiteWins && g.BlackSub == sub) ||
                    (g.Result == GameResult.BlackWins && g.WhiteSub == sub)),
            })
            .FirstOrDefaultAsync(ct);

        return agg is null
            ? new ArenaPlayerStats(0, 0, 0, 0) // у игрока ещё нет партий
            : new ArenaPlayerStats(agg.Total, agg.Wins, agg.Losses, agg.Draws);
    }

    /// <summary>Партия для воспроизведения. Возвращает null, если игрок не был её участником (приватность).</summary>
    public async Task<ArenaGameDetail?> GetForPlayerAsync(Guid id, string sub, CancellationToken ct)
    {
        var g = await db.ArenaGames.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null || (g.WhiteSub != sub && g.BlackSub != sub)) return null;
        var myColor = g.WhiteSub == sub ? PieceColor.White : PieceColor.Black;
        return new ArenaGameDetail(g.Id, g.TournamentId, g.WhiteName, g.BlackName, g.WhiteIsBot, g.BlackIsBot,
            g.Pgn, g.Result, g.EndReason, g.TimeControl, g.PlayedAt, myColor);
    }

    /// <summary>Кэш разбора (JSON), если уже посчитан и игрок — участник партии.</summary>
    public async Task<string?> GetAnalysisJsonAsync(Guid id, string sub, CancellationToken ct)
    {
        var g = await db.ArenaGames.AsNoTracking()
            .Where(x => x.Id == id && (x.WhiteSub == sub || x.BlackSub == sub))
            .Select(x => x.AnalysisJson).FirstOrDefaultAsync(ct);
        return g;
    }

    /// <summary>Сохраняет посчитанный разбор в кэш (идемпотентно перезаписывает).</summary>
    public async Task SaveAnalysisJsonAsync(Guid id, string json, CancellationToken ct)
    {
        var g = await db.ArenaGames.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return;
        g.AnalysisJson = json;
        await db.SaveChangesAsync(ct);
    }
}
