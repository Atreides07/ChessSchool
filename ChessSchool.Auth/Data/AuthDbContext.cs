using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChessSchool.Auth.Data;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuthCode> AuthCodes => Set<AuthCode>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        b.Entity<RefreshToken>().HasIndex(r => r.Token).IsUnique();
        b.Entity<AuthCode>().HasIndex(a => a.Code).IsUnique();

        // SQLite не умеет ORDER BY по DateTimeOffset — храним как long(ticks).
        if (Database.IsSqlite())
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
}
