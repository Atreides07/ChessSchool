namespace ChessSchool.Arena;

/// <summary>Трансляции: поиск турниров для админки, публичные онлайн-доски (games), отдача фонов (media),
/// iCalendar бренд-турнира и 301-редиректы старых путей. Сетевые вызовы — в request-контексте (грабля #12).</summary>
public static class BroadcastEndpoints
{
    public static void MapBroadcastEndpoints(this WebApplication app)
    {
        // ---------------- Поиск популярных турниров для админки трансляций (тонкий клиент /admin/broadcasts/discover) ----------------
        // Сетевой вызов к источнику и перенос изображения — здесь, в request-контексте (не в лайфсайкле Blazor, грабля #12).

        app.MapGet("/admin/api/discovery", async (
            ChessSchool.Arena.Services.TournamentDiscovery discovery,
            ChessSchool.Arena.Services.BroadcastsCatalog catalog,
            CancellationToken ct) =>
        {
            IReadOnlyList<ChessSchool.Arena.Services.TournamentSuggestion> items;
            try { items = await discovery.PopularAsync(ct); }
            catch (ChessSchool.Arena.Services.TournamentDiscoveryException) { return Results.Json(new { error = true }, statusCode: 502); }

            var existing = (await catalog.AllFreshAsync()).Select(b => b.Slug).ToHashSet();
            var result = items.Select(s => new
            {
                s.Slug,
                s.Name,
                dateRange = ChessSchool.Arena.BroadcastFormat.DateRange(s.Start, s.End),
                location = s.Location,
                format = s.Format,
                url = s.Url,
                image = s.ImageUrl,
                s.Live,
                alreadyAdded = existing.Contains(s.Slug),
            });
            return Results.Json(new { items = result });
        }).RequireAuthorization("Admin");

        app.MapPost("/admin/api/discovery/add", async (
            ChessSchool.Contracts.AddSuggestedTournamentRequest body,
            ChessSchool.Arena.Services.TournamentDiscovery discovery,
            ChessSchool.Arena.Services.BroadcastsCatalog catalog,
            ChessSchool.Arena.Services.IImageIngestor ingestor,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            var slug = body?.Slug?.Trim();
            if (string.IsNullOrWhiteSpace(slug)) return Results.BadRequest();

            ChessSchool.Arena.Services.TournamentSuggestion? suggestion;
            try { suggestion = await discovery.BySlugAsync(slug, ct); }
            catch (ChessSchool.Arena.Services.TournamentDiscoveryException) { return Results.Json(new { error = true }, statusCode: 502); }
            if (suggestion is null) return Results.NotFound();

            var broadcast = ChessSchool.Arena.Services.TournamentDiscovery.ToBroadcast(suggestion);

            // Идемпотентность: уже в каталоге — считаем добавленным (повторный клик/гонка между нодами).
            if (await catalog.BySlugAsync(broadcast.Slug) is not null)
                return Results.Json(new { slug = broadcast.Slug, alreadyAdded = true });

            // Переносим изображение в наше хранилище (не зависим от внешнего источника). Сбой переноса не должен
            // ронять добавление — оставляем без картинки, админ задаст её при доклассификации.
            try { broadcast.ImageUrl = await ingestor.EnsureStoredAsync(broadcast.ImageUrl, ct); }
            catch (ChessSchool.Arena.Services.ImageIngestException ex)
            {
                log.LogWarning(ex, "Не удалось перенести изображение турнира {Slug}; добавляем без него.", broadcast.Slug);
                broadcast.ImageUrl = null;
            }

            await catalog.UpsertAsync(broadcast);
            return Results.Json(new { slug = broadcast.Slug, alreadyAdded = false });
        }).RequireAuthorization("Admin").DisableAntiforgery();

        // ---------------- Онлайн-доски трансляции (публичные: контент трансляции открыт и индексируем) ----------------
        // Сетевой опрос источника и разбор PGN — здесь, в request-контексте (не в лайфсайкле Blazor, грабля #12).
        // Тонкий клиент детальной страницы тянет это fetch'ем и рисует доски из FEN без внешних библиотек.

        // Сводка по всем доскам (для сетки): текущая позиция, участники, результат, последний ход. Без полуходов.
        app.MapGet("/api/broadcasts/{slug}/games", async (string slug,
            ChessSchool.Arena.Services.BroadcastLive live, CancellationToken ct) =>
        {
            IReadOnlyList<ChessSchool.Arena.Services.BroadcastBoard>? boards;
            try { boards = await live.GetAsync(slug, ct); }
            catch (ChessSchool.Arena.Services.BroadcastLiveException) { return Results.Json(new { error = true }, statusCode: 502); }
            if (boards is null) return Results.NotFound();

            return Results.Json(new
            {
                boards = boards.Select(b => new
                {
                    board = b.Board,
                    white = b.White,
                    black = b.Black,
                    whiteElo = b.WhiteElo,
                    blackElo = b.BlackElo,
                    result = b.Result,
                    fen = b.Fen,
                    lastFrom = b.LastFrom,
                    lastTo = b.LastTo,
                    plyCount = b.PlyCount,
                    finished = b.Finished,
                }),
            });
        });

        // Полная партия одной доски (для просмотра ходов): стартовый FEN + все полуходы (SAN/FEN/клетки).
        app.MapGet("/api/broadcasts/{slug}/games/{board:int}", async (string slug, int board,
            ChessSchool.Arena.Services.BroadcastLive live, CancellationToken ct) =>
        {
            IReadOnlyList<ChessSchool.Arena.Services.BroadcastBoard>? boards;
            try { boards = await live.GetAsync(slug, ct); }
            catch (ChessSchool.Arena.Services.BroadcastLiveException) { return Results.Json(new { error = true }, statusCode: 502); }
            if (boards is null) return Results.NotFound();

            var b = boards.FirstOrDefault(x => x.Board == board);
            if (b is null) return Results.NotFound();

            return Results.Json(new
            {
                board = b.Board,
                white = b.White,
                black = b.Black,
                whiteElo = b.WhiteElo,
                blackElo = b.BlackElo,
                result = b.Result,
                startFen = b.StartFen,
                plies = b.Plies.Select(p => new { san = p.San, fen = p.Fen, from = p.From, to = p.To }),
            });
        });

        app.MapGet("/majors", () => Results.Redirect("/broadcasts", permanent: true));
        app.MapGet("/majors/{slug}", (string slug) => Results.Redirect($"/broadcasts/{slug}", permanent: true));

        // Отдача загруженных фонов из приватного бакета S3 (нет mixed-content и публичной экспозиции).
        // Ключ иммутабелен (guid) → агрессивное кэширование браузером/CDN снимает нагрузку с приложения.
        app.MapGet("/media/broadcasts/{key}", async (string key, HttpContext ctx,
            ChessSchool.Arena.Services.IImageStorage storage, CancellationToken ct) =>
        {
            if (!ChessSchool.Arena.Services.ImageKinds.IsValidKey(key)) return Results.NotFound();
            var img = await storage.OpenAsync(key, ct);
            if (img is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            return Results.Stream(img.Content, img.ContentType);
        });

        // «Напомнить» для бренд-турнира — iCalendar (.ics): браузер добавит событие в календарь.
        // Без PII, без серверных напоминалок и логина; stateless (мультисервер). Только видимые бренды.
        app.MapGet("/t/{slug}/calendar.ics", async (string slug, HttpRequest r,
            ChessSchool.Arena.Services.BrandTournamentCatalog catalog) =>
        {
            var b = await catalog.BySlugAsync(slug);
            if (b is null || !b.Visible) return Results.NotFound();

            static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
                .Replace("\r\n", "\\n").Replace("\n", "\\n");
            var url = $"{r.Scheme}://{r.Host}/t/{slug}";
            var ics = string.Join("\r\n",
                "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//ChessArena//Brand Tournament//EN", "CALSCALE:GREGORIAN",
                "BEGIN:VEVENT",
                $"UID:{slug}@chessarena",
                $"DTSTAMP:{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMddTHHmmssZ}",
                $"DTSTART:{b.StartsAt.UtcDateTime:yyyyMMddTHHmmssZ}",
                $"DTEND:{b.StartsAt.AddSeconds(b.DurationSeconds).UtcDateTime:yyyyMMddTHHmmssZ}",
                $"SUMMARY:{Esc(b.Name)}",
                $"DESCRIPTION:{Esc(b.Description)}",
                $"URL:{url}",
                "END:VEVENT", "END:VCALENDAR") + "\r\n";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", $"{slug}.ics");
        });
    }
}
