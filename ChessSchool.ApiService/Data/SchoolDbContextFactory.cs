using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChessSchool.ApiService.Data;

/// <summary>
/// Design-time фабрика для `dotnet ef` (генерация/применение миграций).
/// Использует Npgsql с заглушкой connection string — соединение при `migrations add` не открывается,
/// поэтому реальная БД (и Docker) для генерации миграций не нужна.
/// </summary>
public sealed class SchoolDbContextFactory : IDesignTimeDbContextFactory<SchoolDbContext>
{
    public SchoolDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SchoolDbContext>()
            .UseNpgsql("Host=localhost;Database=school;Username=postgres;Password=postgres")
            .Options;
        return new SchoolDbContext(options);
    }
}
