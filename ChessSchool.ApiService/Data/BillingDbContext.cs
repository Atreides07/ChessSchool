using ChessSchool.ApiService.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.ApiService.Data;

/// <summary>
/// БД биллинга (`billingdb`): подписки B2C-премиума и идемпотентность вебхук-событий провайдера. Отдельный
/// bounded-контекст — своя БД, свой набор миграций. Ссылки на пользователя — по строковому IdP-`sub`.
/// </summary>
public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<ProcessedBillingEvent> ProcessedBillingEvents => Set<ProcessedBillingEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Subscription>().HasIndex(s => s.UserSub).IsUnique();          // одна подписка на пользователя
        b.Entity<Subscription>().HasIndex(s => s.ProviderSubscriptionId);     // поиск по id провайдера (вебхук)
        b.Entity<ProcessedBillingEvent>().HasKey(p => p.EventId);             // идемпотентность по event id
        // DateTimeOffset хранится нативно в PostgreSQL (timestamptz) — конвертеры не нужны.
    }
}
