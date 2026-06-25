using ChessSchool.ApiService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Student>().HasIndex(s => s.LinkedUserSub);
        b.Entity<Game>().HasIndex(g => g.ExternalGameId).IsUnique();
        b.Entity<ShareLink>().HasIndex(s => s.Token).IsUnique();
        b.Entity<RatingPoint>().HasIndex(r => r.StudentId);

        // SQLite не умеет ORDER BY по DateTimeOffset — храним как long(ticks).
        // Для PostgreSQL (прод) оставляем нативный timestamptz.
        if (Database.IsSqlite())
            SqliteConversions.ApplyDateTimeOffsetAsTicks(b);
    }
}

/// <summary>Конвертеры, нужные только провайдеру SQLite.</summary>
public static class SqliteConversions
{
    public static void ApplyDateTimeOffsetAsTicks(ModelBuilder b)
    {
        var converter = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));

        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
                if (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?))
                    prop.SetValueConverter(converter);
    }
}
