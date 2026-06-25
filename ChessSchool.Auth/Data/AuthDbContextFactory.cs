using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChessSchool.Auth.Data;

/// <summary>
/// Design-time фабрика для `dotnet ef` (генерация/применение миграций).
/// Включает <c>UseOpenIddict()</c>, чтобы таблицы OpenIddict попадали в миграции.
/// Connection string — заглушка: при `migrations add` соединение не открывается,
/// поэтому реальная БД (и Docker) для генерации миграций не нужна.
/// </summary>
public sealed class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Database=auth;Username=postgres;Password=postgres")
            .UseOpenIddict()
            .Options;
        return new AuthDbContext(options);
    }
}
