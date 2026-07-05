using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChessSchool.ApiService.Data;

/// <summary>Design-time фабрика для `dotnet ef` (миграции BillingDbContext). Соединение при генерации не открывается.</summary>
public sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BillingDbContext>()
            .UseNpgsql("Host=localhost;Database=billing;Username=postgres;Password=postgres")
            .Options;
        return new BillingDbContext(options);
    }
}
