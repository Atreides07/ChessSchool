namespace ChessSchool.Arena;

/// <summary>Импорт жеребьёвки chess-results (публичный инструмент /pairings): парсинг .xlsx и фетч по ссылке
/// с проверкой хоста (SSRF). Сетевой вызов — в request-контексте, не в рендере Blazor (грабля #12).</summary>
public static class PairingsEndpoints
{
    public static void MapPairingsEndpoints(this WebApplication app)
    {
        // ---------------- Импорт жеребьёвки из chess-results (публичный инструмент /pairings) ----------------
        // Парсинг файла и сетевой фетч — здесь, в request-контексте (не в рендере Blazor, грабля #12). Тонкий клиент
        // (js/pairings.js) тянет JSON, держит модель в браузере, правит пары и экспортирует — состояние не на сервере.

        // Хост chess-results — единственный допустимый источник для фетча по ссылке (защита от SSRF).
        static bool IsChessResultsHost(string host) =>
            host.Equals("chess-results.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".chess-results.com", StringComparison.OrdinalIgnoreCase);

        // Если в ссылке не задан вид (art), запрашиваем «Пары» всех туров целиком (без постраничности).
        static Uri EnsurePairingView(Uri uri)
        {
            if (uri.Query.Contains("art=", StringComparison.OrdinalIgnoreCase)) return uri;
            var sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            return new Uri(uri + sep + "art=2&turdet=YES&zeilen=99999");
        }

        // Ручное следование редиректам с проверкой хоста на каждом хопе (авто-редирект выключен — иначе SSRF).
        static async Task<string> FetchChessResults(HttpClient client, Uri uri, CancellationToken ct)
        {
            for (int hop = 0; hop < 4; hop++)
            {
                using var resp = await client.GetAsync(uri, ct);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                    if (!IsChessResultsHost(next.Host)) throw new InvalidOperationException("Редирект на сторонний хост.");
                    uri = next;
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            throw new InvalidOperationException("Слишком много редиректов.");
        }

        // Разбор загруженного .xlsx «Пары/Результаты».
        app.MapPost("/api/pairings/parse", async (HttpRequest req, ILogger<Program> log) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "Ожидался файл (multipart/form-data)." });
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Файл не передан." });
            if (file.Length > 8 * 1024 * 1024) return Results.BadRequest(new { error = "Файл слишком большой (макс 8 МБ)." });
            try
            {
                // ZipArchive требует seekable-поток → копируем в память (файл маленький, лимит 8 МБ выше).
                await using var src = file.OpenReadStream();
                using var ms = new MemoryStream();
                await src.CopyToAsync(ms);
                ms.Position = 0;
                return Results.Ok(ChessSchool.Arena.Services.ChessResultsParser.ParseXlsx(ms));
            }
            catch (ChessSchool.Arena.Services.PairingParseException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Не удалось разобрать .xlsx жеребьёвки.");
                return Results.BadRequest(new { error = "Не удалось прочитать файл. Это выгрузка «Пары/Результаты» из chess-results в .xlsx?" });
            }
        }).DisableAntiforgery();

        // Подтягивание жеребьёвки по ссылке на турнир chess-results.
        app.MapPost("/api/pairings/fetch", async (PairingFetchRequest body, IHttpClientFactory http, ILogger<Program> log, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Url) || !Uri.TryCreate(body.Url.Trim(), UriKind.Absolute, out var uri))
                return Results.BadRequest(new { error = "Укажите ссылку на турнир chess-results." });
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                return Results.BadRequest(new { error = "Поддерживаются только http(s)-ссылки." });
            if (!IsChessResultsHost(uri.Host))
                return Results.BadRequest(new { error = "Поддерживаются только ссылки на chess-results.com." });

            uri = EnsurePairingView(uri);
            var client = http.CreateClient("PairingFetch");
            try
            {
                var html = await FetchChessResults(client, uri, ct);
                return Results.Ok(ChessSchool.Arena.Services.ChessResultsParser.ParseHtml(html, uri.ToString()));
            }
            catch (ChessSchool.Arena.Services.PairingParseException ex)
            {
                return Results.BadRequest(new { error = ex.Message + " Попробуйте загрузить файл .xlsx из chess-results." });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Не удалось подтянуть жеребьёвку с {Url}.", uri);
                return Results.BadRequest(new { error = "Не удалось получить страницу турнира. Проверьте ссылку или загрузите .xlsx." });
            }
        }).DisableAntiforgery();
    }
}

// Тело запроса фетча жеребьёвки по ссылке (top-level — чтобы minimal-API биндил его из тела запроса).
record PairingFetchRequest(string? Url);
