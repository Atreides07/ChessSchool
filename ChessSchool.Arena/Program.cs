using ChessSchool.Arena.Components;
using ChessSchool.WebAuth;
using Orleans.Configuration;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// За обратным прокси (Aspire/ingress) доверяем forwarded-заголовкам — иначе OIDC redirect_uri
// строился бы по внутреннему порту Kestrel, а не по внешнему хосту.
builder.AddChessSchoolForwardedHeaders();

// Локализация RU/EN (культура из ?culture=/cookie/Accept-Language).
builder.AddChessSchoolLocalization();

// Единый вход (SSO) — тот же аккаунт, что и в ChessSchool.
builder.AddChessSchoolSso();

// Co-hosted Orleans silo: турнирные грейны живут прямо в этом процессе.
// Компоненты Blazor вызывают грейны напрямую через IGrainFactory.
// Отдельный кластер силоса арены (изолирован от GameServer по портам и clusterId).
// Есть Redis → кластеризация и хранилище турниров в Redis: состояние (таблица/история/мета)
// переживает перезапуск и масштабирование силосов, грейн турнира — единственная активация в кластере.
// Нет → localhost-кластер + in-memory storage (dev, одна нода).
var redisConn = builder.Configuration.GetRedisConnectionString(builder.Environment);
var siloPort = builder.Configuration.GetValue("Orleans:SiloPort", 11112);
var gatewayPort = builder.Configuration.GetValue("Orleans:GatewayPort", 30001);
builder.UseOrleans(silo =>
{
    if (redisConn is not null)
    {
        silo.UseRedisClustering(o => o.ConfigurationOptions = ConfigurationOptions.Parse(redisConn));
        silo.Configure<ClusterOptions>(o => { o.ClusterId = "chessschool-arena"; o.ServiceId = "chessschool-arena"; });
        silo.ConfigureEndpoints(siloPort: siloPort, gatewayPort: gatewayPort);
        silo.AddRedisGrainStorage("arena", o => o.ConfigurationOptions = ConfigurationOptions.Parse(redisConn));
        // Reminders в Redis: грейн турнира «воскресает» на любой ноде даже при потере текущей.
        silo.UseRedisReminderService(o => o.ConfigurationOptions = ConfigurationOptions.Parse(redisConn));
    }
    else
    {
        silo.UseLocalhostClustering(
            siloPort: siloPort, gatewayPort: gatewayPort,
            serviceId: "chessschool-arena", clusterId: "chessschool-arena");
        silo.AddMemoryGrainStorage("arena");
    }
});

// Рантайм-переключатели грейна (reminders доступны только при настроенном Redis-сервисе).
builder.Services.AddSingleton(new ChessSchool.Arena.Services.ArenaRuntimeOptions(RemindersEnabled: redisConn is not null));

// Readiness-проверка Redis (в /health, не в /alive).
if (redisConn is not null) builder.Services.AddHealthChecks().AddRedis(redisConn, name: "redis");

// Внутрипроцессный pub/sub для push-обновлений турниров (грейн → компоненты/хаб).
builder.Services.AddSingleton<ChessSchool.Arena.Services.ArenaNotifier>();

// SignalR-хаб страницы турнира (тонкий клиент). Backplane НЕ нужен: кросс-нодовость обеспечивает
// ArenaNotifier (Redis pub/sub) — каждая нода рассылает своим локальным соединениям (см. ArenaBroadcaster).
builder.Services.AddSignalR();
builder.Services.AddSingleton<ChessSchool.Arena.Services.ArenaBroadcaster>();

// Каталог бренд-турниров (грейн + пер-нодовый кэш). Он же решает индексацию (IBrandTournaments):
// бренд-турниры индексируются и попадают в «Главные»/sitemap, регулярные (расписание) — нет.
builder.Services.AddSingleton<ChessSchool.Arena.Services.BrandTournamentCatalog>();
builder.Services.AddSingleton<ChessSchool.Arena.Services.IBrandTournaments>(
    sp => sp.GetRequiredService<ChessSchool.Arena.Services.BrandTournamentCatalog>());

// Каталог трансляций: источник истины — грейн (Redis grain storage), на ноде — кэш с TTL поверх него.
builder.Services.AddSingleton<ChessSchool.Arena.Services.BroadcastsCatalog>();

// Авторизация админки (/admin): доступ только у e-mail из конфигурации Admin:Emails (через запятую).
// В Development при пустом списке админом считается любой аутентифицированный пользователь (удобство dev);
// вне Development пустой список = доступ закрыт всем (безопасный дефолт production-ready).
var adminEmails = (builder.Configuration["Admin:Emails"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var adminFallbackAny = builder.Environment.IsDevelopment();
builder.Services.AddAuthorizationBuilder().AddPolicy("Admin", policy =>
    policy.RequireAssertion(ctx =>
    {
        if (ctx.User.Identity?.IsAuthenticated != true) return false;
        if (adminEmails.Length == 0) return adminFallbackAny;
        var email = ctx.User.FindFirst("email")?.Value
            ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        return email is not null && adminEmails.Contains(email, StringComparer.OrdinalIgnoreCase);
    }));

// Серверный шахматный движок (Stockfish) для ботов.
builder.Services.AddSingleton<ChessSchool.Arena.Services.IChessEngine, ChessSchool.Arena.Services.StockfishEngine>();
// Отдельный инстанс движка (свой процесс/семафор) для разбора партий — чтобы анализ не конкурировал
// с ходами ботов в живой игре.
builder.Services.AddSingleton<ChessSchool.Arena.Services.IPositionEvaluator>(sp =>
    new ChessSchool.Arena.Services.StockfishEngine(
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<ChessSchool.Arena.Services.StockfishEngine>>()));

// Хранилище фоновых изображений трансляций: S3 (реальный в проде, MinIO локально) при наличии конфига
// Storage:S3, иначе заглушка (загрузка недоступна, но поле URL остаётся рабочим). Бакет приватный —
// файлы отдаются через собственный эндпоинт /media (нет mixed-content и публичной экспозиции бакета).
var s3Options = builder.Configuration.GetSection("Storage:S3").Get<ChessSchool.Arena.Services.S3Options>() ?? new();
if (s3Options.IsConfigured)
{
    builder.Services.AddSingleton(s3Options);
    builder.Services.AddSingleton<ChessSchool.Arena.Services.IImageStorage, ChessSchool.Arena.Services.S3ImageStorage>();
}
else
{
    builder.Services.AddSingleton<ChessSchool.Arena.Services.IImageStorage, ChessSchool.Arena.Services.NullImageStorage>();
}

// Перенос внешних URL изображений в наше хранилище (картинку нельзя подменить на чужой стороне).
// Клиент без авто-редиректа: редиректы следуем вручную, проверяя хост на каждом хопе (SSRF).
builder.Services.AddHttpClient(ChessSchool.Arena.Services.ImageIngestor.HttpClientName, c =>
        c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    });
builder.Services.AddSingleton<ChessSchool.Arena.Services.IImageIngestor, ChessSchool.Arena.Services.ImageIngestor>();
// Разовый перенос уже сохранённых внешних URL изображений в S3 (для записей до появления переноса).
builder.Services.AddHostedService<ChessSchool.Arena.Services.ImageIngestBackfill>();

// Продуктовая аналитика (PostHog при наличии ключа, иначе no-op).
builder.AddChessSchoolAnalytics();

// Доступ к cookie запроса (запоминание выбранного вида расписания при SSR-навигации).
builder.Services.AddHttpContextAccessor();

// Премиум-подписка игрока: статус берём из ApiService (источник истины) по внутреннему ключу,
// кэшируем на ноду. Ключ инжектит AppHost (InternalApiKey); вне Development обязателен (fail-fast).
builder.Services.AddMemoryCache();
var internalApiKey = builder.Configuration.ResolveInternalApiKey(builder.Environment);
builder.Services.AddHttpClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName,
    c => c.BaseAddress = new("https+http://apiservice"));
builder.Services.AddSingleton<ChessSchool.Arena.Services.IPlayerEntitlements>(sp =>
    new ChessSchool.Arena.Services.PlayerEntitlements(
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
        internalApiKey,
        sp.GetRequiredService<ILogger<ChessSchool.Arena.Services.PlayerEntitlements>>()));

// Архив завершённых арена-партий в ApiService (для истории/разбора).
builder.Services.AddHttpClient(ChessSchool.Arena.Services.ArenaGameArchiveClient.HttpClientName,
    c => c.BaseAddress = new("https+http://apiservice"));
builder.Services.AddSingleton<ChessSchool.Arena.Services.IArenaGameArchiveClient>(sp =>
    new ChessSchool.Arena.Services.ArenaGameArchiveClient(
        sp.GetRequiredService<IHttpClientFactory>(), internalApiKey,
        sp.GetRequiredService<ILogger<ChessSchool.Arena.Services.ArenaGameArchiveClient>>()));

// История и разбор партий (премиум-фича): клиент к ApiService + сервис разбора (Stockfish) + оркестратор.
builder.Services.AddSingleton(sp => new ChessSchool.Arena.Services.ArenaGamesApiClient(
    sp.GetRequiredService<IHttpClientFactory>(), internalApiKey,
    sp.GetRequiredService<ILogger<ChessSchool.Arena.Services.ArenaGamesApiClient>>()));
builder.Services.AddSingleton<ChessSchool.Arena.Services.GameAnalysisService>();
builder.Services.AddSingleton<ChessSchool.Arena.Services.ArenaReviewService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Сжатие ДИНАМИЧЕСКИХ ответов (SSR-HTML главной ~60KB и enhanced-nav фетчи). Статику жмёт MapStaticAssets,
// а HTML без этого уходил несжатым → на медленном канале (3G) ~секунды до первого кадра. Уровень Fastest:
// быстрое сжатие без заметного CPU на запрос; на текстовом HTML даёт ~5-8x. EnableForHttps — HTML без секретов.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

var app = builder.Build();

app.UseResponseCompression(); // как можно раньше: до отдачи статики и эндпоинтов
app.UseForwardedHeaders(); // как можно раньше: схема/хост из X-Forwarded-* до построения redirect_uri
app.UseChessSchoolLocalization(); // культура запроса + эндпоинт /lang

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapSsoEndpoints();

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

// Раздел переименован «Турниры» → «Трансляции»: старые пути 301-редиректятся на /broadcasts (без битых ссылок).
// Dev-активация премиума без оплаты (только Development) — проксирует в ApiService dev-activate.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/premium/dev-activate", async (HttpContext ctx, IHttpClientFactory http,
        ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
    {
        var sub = ctx.User.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
        var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/subscriptions/dev-activate");
        req.Headers.Add("X-Internal-Key", internalApiKey);
        req.Content = System.Net.Http.Json.JsonContent.Create(new ChessSchool.Contracts.DevActivateRequest(sub, "premium"));
        await client.SendAsync(req, ct);
        ents.Invalidate(sub); // сбросить кэш — статус подхватится на ближайшем запросе/перезагрузке
        return Results.Ok();
    }).RequireAuthorization().DisableAntiforgery();
}

// Управление подпиской: редирект в hosted Customer Portal провайдера (URL берём у ApiService).
app.MapGet("/premium/portal", async (HttpContext ctx, IHttpClientFactory http, CancellationToken ct) =>
{
    var sub = ctx.User.FindFirst("sub")?.Value;
    if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
    var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
    using var req = new HttpRequestMessage(HttpMethod.Get, $"/internal/subscriptions/{Uri.EscapeDataString(sub)}/portal");
    req.Headers.Add("X-Internal-Key", internalApiKey);
    using var resp = await client.SendAsync(req, ct);
    if (resp.IsSuccessStatusCode)
    {
        var link = await resp.Content.ReadFromJsonAsync<ChessSchool.Contracts.PortalLinkDto>(ct);
        if (!string.IsNullOrEmpty(link?.Url)) return Results.Redirect(link.Url);
    }
    return Results.Redirect("/premium"); // портал недоступен (dev/нет клиента) — назад
}).RequireAuthorization();

// Вытягивание статуса: после возврата с checkout (есть txn) reconcile по транзакции, иначе — refresh
// по сохранённой подписке. Спасает, если вебхук Paddle не дошёл/опоздал.
app.MapPost("/premium/reconcile", async (HttpContext ctx, string? txn, IHttpClientFactory http,
    ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
{
    var sub = ctx.User.FindFirst("sub")?.Value;
    if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
    var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
    using var req = string.IsNullOrEmpty(txn)
        ? new HttpRequestMessage(HttpMethod.Post, $"/internal/subscriptions/{Uri.EscapeDataString(sub)}/refresh")
        : new HttpRequestMessage(HttpMethod.Post, "/internal/subscriptions/reconcile-transaction")
        { Content = System.Net.Http.Json.JsonContent.Create(new ChessSchool.Contracts.ReconcileTxnRequest(txn)) };
    req.Headers.Add("X-Internal-Key", internalApiKey);
    try { await client.SendAsync(req, ct); } catch { /* недоступность ApiService — не падаем */ }
    ents.Invalidate(sub); // статус мог измениться — сбросить кэш ноды, чтобы reload показал актуальное
    return Results.Ok();
}).RequireAuthorization().DisableAntiforgery();

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

// Хаб страницы турнира (тонкий браузерный клиент подключается по тому же origin → cookie-auth).
app.MapHub<ChessSchool.Arena.Hubs.ArenaHub>("/arenahub");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
