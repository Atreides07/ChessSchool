using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Domain;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.ApiService.Services;

/// <summary>
/// Сохраняет завершённую партию и обновляет рейтинги/счётчики учеников.
/// Используется и для онлайн-партий (по UserSub), и для ручной атрибуции тренировочных.
/// </summary>
public sealed class GameArchiver(SchoolDbContext db, IRatingService rating)
{
    /// <summary>Сопоставляет онлайн-партию с учениками по UserSub и применяет рейтинг.</summary>
    public async Task<bool> ArchiveOnlineAsync(ArchiveGameRequest req, CancellationToken ct)
    {
        // Идемпотентность: повтор от GameServer не создаёт дубль.
        if (await db.Games.AnyAsync(g => g.ExternalGameId == req.GameId, ct))
            return false;

        var white = await db.Students.FirstOrDefaultAsync(s => s.LinkedUserSub == req.WhiteUserSub, ct);
        var black = await db.Students.FirstOrDefaultAsync(s => s.LinkedUserSub == req.BlackUserSub, ct);

        var game = new Game
        {
            Source = AttributionSource.OnlineMatch,
            Status = GameStatus.Finished,
            PlayedAt = req.FinishedAt,
            Pgn = req.Pgn,
            Result = req.Result,
            EndReason = req.EndReason,
            ExternalGameId = req.GameId,
            WhiteStudentId = white?.Id,
            BlackStudentId = black?.Id
        };

        ApplyRating(game, white, black);
        db.Games.Add(game);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Применяет результат к уже существующей партии (ручная атрибуция тренером).</summary>
    public async Task AttributeAsync(Game game, Student white, Student black, GameResult result, CancellationToken ct)
    {
        game.WhiteStudentId = white.Id;
        game.BlackStudentId = black.Id;
        game.Result = result;
        game.Status = GameStatus.Finished;
        game.Source = AttributionSource.CheckIn;
        ApplyRating(game, white, black);
        await db.SaveChangesAsync(ct);
    }

    private void ApplyRating(Game game, Student? white, Student? black)
    {
        // Рейтинг неизвестного соперника (гостя) — по дефолту Glicko-2, но обновляем только учеников.
        var whiteIn = white is not null
            ? new PlayerRating(white.Rating, white.RatingDeviation, white.Volatility)
            : new PlayerRating(1500, 350, 0.06);
        var blackIn = black is not null
            ? new PlayerRating(black.Rating, black.RatingDeviation, black.Volatility)
            : new PlayerRating(1500, 350, 0.06);

        var (whiteUpd, blackUpd) = rating.Compute(whiteIn, blackIn, game.Result);

        game.WhiteRatingChange = whiteUpd.Delta;
        game.BlackRatingChange = blackUpd.Delta;

        if (white is not null) UpdateStudent(white, whiteUpd, game.Result, isWhite: true, game.PlayedAt);
        if (black is not null) UpdateStudent(black, blackUpd, game.Result, isWhite: false, game.PlayedAt);
    }

    private void UpdateStudent(Student s, RatingUpdate u, GameResult result, bool isWhite, DateTimeOffset at)
    {
        s.Rating = u.Rating;
        s.RatingDeviation = (int)Math.Round(u.Rd);
        s.Volatility = u.Volatility;
        s.GamesPlayed++;
        bool won = (isWhite && result == GameResult.WhiteWins) || (!isWhite && result == GameResult.BlackWins);
        bool lost = (isWhite && result == GameResult.BlackWins) || (!isWhite && result == GameResult.WhiteWins);
        if (result == GameResult.Draw) s.Draws++;
        else if (won) s.Wins++;
        else if (lost) s.Losses++;

        db.RatingPoints.Add(new RatingPoint { StudentId = s.Id, Date = at, Rating = u.Rating });
    }
}
