namespace ChessSchool.Arena;

/// <summary>SEO-эндпоинты: robots.txt и sitemap.xml (хост из запроса, за прокси — по forwarded-заголовкам).</summary>
public static class SeoEndpoints
{
    public static void MapSeoEndpoints(this WebApplication app)
    {
        // SEO: robots.txt и sitemap.xml. Хост берём из запроса (за прокси корректен благодаря forwarded headers),
        // поэтому абсолютные URL верны без хардкода домена.
        app.MapGet("/robots.txt", (HttpRequest r) =>
        {
            var b = $"{r.Scheme}://{r.Host}";
            return Results.Text(
                $"User-agent: *\nAllow: /\nDisallow: /admin\nDisallow: /signin\nDisallow: /signout\nSitemap: {b}/sitemap.xml\n",
                "text/plain");
        });
        app.MapGet("/sitemap.xml", async (HttpRequest r, ChessSchool.Arena.Services.BroadcastsCatalog catalog,
            ChessSchool.Arena.Services.IBrandTournaments brand) =>
        {
            var b = $"{r.Scheme}://{r.Host}";
            var paths = new List<string> { "", "broadcasts" };
            // Только видимые трансляции — скрытые не должны попадать в индекс.
            paths.AddRange((await catalog.PublicAsync()).Select(m => $"broadcasts/{m.Slug}"));
            // Бренд-турниры (индексируемые); регулярные турниры расписания в sitemap не попадают.
            paths.AddRange((await brand.ListIndexableAsync()).Select(t => $"t/{t.Slug}"));
            var locs = string.Join("\n", paths.Select(u => $"  <url><loc>{b}/{u}</loc></url>"));
            return Results.Text(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n{locs}\n</urlset>\n",
                "application/xml");
        });
    }
}
