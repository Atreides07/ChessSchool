using ChessSchool.ApiService.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.ApiService.Data;

/// <summary>
/// БД арены (`arenadb`): архив завершённых арена-партий. Отдельный bounded-контекст от школы — своя БД,
/// свой набор миграций. Кросс-контекстных FK нет (игроки — по строковому IdP-`sub`), джойнов со школой нет.
/// </summary>
public class ArenaDbContext(DbContextOptions<ArenaDbContext> options) : DbContext(options)
{
    public DbSet<ArenaGame> ArenaGames => Set<ArenaGame>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Идемпотентность архивации арена-партий + история по каждому игроку (sub) от свежих к старым.
        b.Entity<ArenaGame>().HasIndex(g => g.ExternalGameId).IsUnique();
        b.Entity<ArenaGame>().HasIndex(g => new { g.WhiteSub, g.PlayedAt });
        b.Entity<ArenaGame>().HasIndex(g => new { g.BlackSub, g.PlayedAt });
        // TimeControl — value-объект (initial+increment) → две колонки, без отдельной таблицы/ключа.
        b.Entity<ArenaGame>().OwnsOne(g => g.TimeControl);
        // DateTimeOffset хранится нативно в PostgreSQL (timestamptz) — конвертеры не нужны.
    }
}
