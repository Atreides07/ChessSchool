using ChessSchool.ApiService.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.ApiService.Data;

public class SchoolDbContext(DbContextOptions<SchoolDbContext> options) : DbContext(options)
{
    public DbSet<School> Schools => Set<School>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<RatingPoint> RatingPoints => Set<RatingPoint>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<ProcessedBillingEvent> ProcessedBillingEvents => Set<ProcessedBillingEvent>();
    public DbSet<ArenaGame> ArenaGames => Set<ArenaGame>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Subscription>().HasIndex(s => s.UserSub).IsUnique();          // одна подписка на пользователя
        b.Entity<Subscription>().HasIndex(s => s.ProviderSubscriptionId);     // поиск по id провайдера (вебхук)
        b.Entity<ProcessedBillingEvent>().HasKey(p => p.EventId);             // идемпотентность по event id
        b.Entity<Student>().HasIndex(s => s.LinkedUserSub);
        b.Entity<Student>().HasIndex(s => s.GroupId);                 // листинг учеников школы по группам
        b.Entity<Game>().HasIndex(g => g.ExternalGameId).IsUnique();
        b.Entity<Game>().HasIndex(g => g.WhiteStudentId);            // история партий ученика
        b.Entity<Game>().HasIndex(g => g.BlackStudentId);
        b.Entity<Game>().HasIndex(g => new { g.Source, g.PlayedAt }); // очередь атрибуции (необатрибутир.)
        b.Entity<ShareLink>().HasIndex(s => s.Token).IsUnique();
        b.Entity<RatingPoint>().HasIndex(r => r.StudentId);
        // Идемпотентность архивации арена-партий + история по каждому игроку (sub) от свежих к старым.
        b.Entity<ArenaGame>().HasIndex(g => g.ExternalGameId).IsUnique();
        b.Entity<ArenaGame>().HasIndex(g => new { g.WhiteSub, g.PlayedAt });
        b.Entity<ArenaGame>().HasIndex(g => new { g.BlackSub, g.PlayedAt });
        // TimeControl — value-объект (initial+increment) → две колонки, без отдельной таблицы/ключа.
        b.Entity<ArenaGame>().OwnsOne(g => g.TimeControl);
        // DateTimeOffset хранится нативно в PostgreSQL (timestamptz) — конвертеры не нужны.
    }
}
