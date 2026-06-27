using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

/// <summary>
/// Контракт точки расширения индексации: пока брендов нет (NoBrandTournaments), любой турнир
/// неиндексируемый и sitemap турниров не содержит — регулярные турниры расписания не утекают в индекс.
/// </summary>
public class BrandTournamentsTests
{
    [Theory]
    [InlineData("blitz-1719500000")]      // регулярный id расписания
    [InlineData("spring-blitz-cup-2026")] // даже похожий на бренд slug — без каталога не индексируется
    public async Task NoBrand_TreatsEverythingAsNonIndexable(string id)
    {
        IBrandTournaments brand = new NoBrandTournaments();
        Assert.False(await brand.IsBrandAsync(id));
    }

    [Fact]
    public async Task NoBrand_SitemapHasNoTournaments()
    {
        IBrandTournaments brand = new NoBrandTournaments();
        Assert.Empty(await brand.ListIndexableAsync());
    }
}
