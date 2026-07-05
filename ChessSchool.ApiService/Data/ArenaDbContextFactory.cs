using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChessSchool.ApiService.Data;

/// <summary>Design-time фабрика для `dotnet ef` (миграции ArenaDbContext). Соединение при генерации не открывается.</summary>
public sealed class ArenaDbContextFactory : IDesignTimeDbContextFactory<ArenaDbContext>
{
    public ArenaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ArenaDbContext>()
            .UseNpgsql("Host=localhost;Database=arena;Username=postgres;Password=postgres")
            .Options;
        return new ArenaDbContext(options);
    }
}
