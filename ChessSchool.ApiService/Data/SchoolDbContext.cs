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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Student>().HasIndex(s => s.LinkedUserSub);
        b.Entity<Game>().HasIndex(g => g.ExternalGameId).IsUnique();
        b.Entity<ShareLink>().HasIndex(s => s.Token).IsUnique();
        b.Entity<RatingPoint>().HasIndex(r => r.StudentId);
        // DateTimeOffset хранится нативно в PostgreSQL (timestamptz) — конвертеры не нужны.
    }
}
