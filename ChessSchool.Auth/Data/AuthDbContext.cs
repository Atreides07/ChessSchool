using Microsoft.EntityFrameworkCore;

namespace ChessSchool.Auth.Data;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuthCode> AuthCodes => Set<AuthCode>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<AuthEvent> AuthEvents => Set<AuthEvent>();
    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        b.Entity<RefreshToken>().HasIndex(r => r.Token).IsUnique();
        b.Entity<AuthCode>().HasIndex(a => a.Code).IsUnique();
        b.Entity<EmailToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<EmailToken>().HasIndex(t => new { t.UserId, t.Purpose });
        // Аудит: выборки по пользователю и по времени (расследование инцидентов, детект аномалий).
        b.Entity<AuthEvent>().HasIndex(e => new { e.Email, e.CreatedAt });
        b.Entity<AuthEvent>().HasIndex(e => new { e.UserId, e.CreatedAt });
        b.Entity<AuthEvent>().HasIndex(e => e.CreatedAt);
        b.Entity<MfaRecoveryCode>().HasIndex(c => c.UserId);
        b.Entity<MfaRecoveryCode>().HasIndex(c => c.CodeHash);
        // DateTimeOffset хранится нативно в PostgreSQL (timestamptz) — конвертеры не нужны.
    }
}
