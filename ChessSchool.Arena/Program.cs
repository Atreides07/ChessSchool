using System.Net.Http.Json;
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

// Авторизация админки (/admin): только роль "admin". Источник истины — IdP: он кладёт claim role=admin
// в токен для админских e-mail (по умолчанию akhmed@outlook.com, список — Admin:Emails в Auth).
// RoleClaimType="role" задаётся в AddChessSchoolSso, поэтому RequireRole видит этот claim.
builder.Services.AddAuthorizationBuilder().AddPolicy("Admin", policy => policy.RequireRole("admin"));

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

// Поиск популярных турниров для админки трансляций (курируемый топ официальных трансляций lichess).
// База источника настраивается конфигом; вызывается из request-контекста minimal-API (не из Blazor).
var lichessBaseUrl = builder.Configuration["Discovery:LichessBaseUrl"] ?? "https://lichess.org";
builder.Services.AddHttpClient(ChessSchool.Arena.Services.TournamentDiscovery.HttpClientName, c =>
{
    c.BaseAddress = new(lichessBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ChessArena/1.0 (broadcast discovery)");
});
builder.Services.AddSingleton<ChessSchool.Arena.Services.TournamentDiscovery>();

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

// Данные партии для тонкого клиента страницы /me/games/{id}: позиции (стартовый FEN + FEN/ход после
// каждого полухода), имена, премиум-статус и кэш разбора. Грузится браузером (fetch) — НЕ в рендере
// Blazor-компонента (там исходящий HTTP зависает; здесь обычный request-контекст — работает).
app.MapGet("/api/me/games/{id:guid}", async (Guid id, HttpContext ctx,
    ChessSchool.Arena.Services.ArenaReviewService review,
    ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
{
    var sub = ctx.User.FindFirst("sub")?.Value;
    if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
    var detail = await review.GetAsync(id, sub, ct);
    if (detail is null) return Results.NotFound();

    var (startFen, plies) = ChessSchool.Arena.Services.GameReplay.FromPgn(detail.Pgn);
    var premium = await ents.IsPremiumAsync(sub, ct);
    var analysis = premium ? await review.GetCachedAnalysisAsync(id, sub, ct) : null;

    // Исход с точки зрения игрока (0 победа / 1 поражение / 2 ничья — как PlayerOutcome).
    var outcome = detail.Result switch
    {
        ChessSchool.Contracts.GameResult.WhiteWins => detail.MyColor == ChessSchool.Contracts.PieceColor.White ? 0 : 1,
        ChessSchool.Contracts.GameResult.BlackWins => detail.MyColor == ChessSchool.Contracts.PieceColor.Black ? 0 : 1,
        _ => 2,
    };

    return Results.Ok(new
    {
        startFen,
        plies = plies.Select(p => new { fen = p.Fen, san = p.San, from = p.From, to = p.To }),
        myColor = detail.MyColor == ChessSchool.Contracts.PieceColor.White ? "w" : "b",
        whiteName = detail.WhiteName,
        blackName = detail.BlackName,
        whiteIsBot = detail.WhiteIsBot,
        blackIsBot = detail.BlackIsBot,
        outcome,
        endReason = (int)detail.EndReason,
        premium,
        analysis,
    });
}).RequireAuthorization();

// Разбор партии для тонкого клиента страницы /me/games/{id}: считается в обычном request-контексте
// (Stockfish/HTTP к ApiService тут работают, в отличие от Blazor-рендерера), кэшируется в ApiService.
// Премиум-фича → гейт по подписке; только участник (GetAsync вернёт null постороннему).
app.MapGet("/api/me/games/{id:guid}/analysis", async (Guid id, HttpContext ctx,
    ChessSchool.Arena.Services.ArenaReviewService review,
    ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
{
    var sub = ctx.User.FindFirst("sub")?.Value;
    if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
    if (!await ents.IsPremiumAsync(sub, ct)) return Results.Forbid();
    var detail = await review.GetAsync(id, sub, ct);
    if (detail is null) return Results.NotFound();

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(120)); // разбор не должен висеть бесконечно
    var analysis = await review.ComputeAnalysisAsync(id, sub, detail.Pgn, timeout.Token);
    return Results.Ok(analysis);
}).RequireAuthorization();

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

// Вытягивание статуса (если вебхук Paddle не дошёл/опоздал). Сначала точный путь по транзакции (если
// есть txn из success-URL), затем надёжное восстановление по e-mail пользователя — оно срабатывает,
// даже когда у нас нет строки подписки и не сохранён txn (например, после ручного снятия в админке).
app.MapPost("/premium/reconcile", async (HttpContext ctx, string? txn, IHttpClientFactory http,
    ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
{
    var sub = ctx.User.FindFirst("sub")?.Value;
    if (string.IsNullOrEmpty(sub)) return Results.Unauthorized();
    var email = ctx.User.FindFirst("email")?.Value
        ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
    var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);

    // 1) Точный путь: по transaction id из возврата с checkout.
    if (!string.IsNullOrEmpty(txn))
    {
        using var rt = new HttpRequestMessage(HttpMethod.Post, "/internal/subscriptions/reconcile-transaction")
        { Content = System.Net.Http.Json.JsonContent.Create(new ChessSchool.Contracts.ReconcileTxnRequest(txn)) };
        rt.Headers.Add("X-Internal-Key", internalApiKey);
        try { await client.SendAsync(rt, ct); } catch { /* недоступность ApiService — не падаем */ }
    }

    // 2) Safety net: refresh по сохранённой подписке, а если её нет — по e-mail пользователя.
    var refreshUrl = $"/internal/subscriptions/{Uri.EscapeDataString(sub)}/refresh"
        + (string.IsNullOrEmpty(email) ? "" : $"?email={Uri.EscapeDataString(email)}");
    using var rf = new HttpRequestMessage(HttpMethod.Post, refreshUrl);
    rf.Headers.Add("X-Internal-Key", internalApiKey);
    try { await client.SendAsync(rf, ct); } catch { /* недоступность ApiService — не падаем */ }

    ents.Invalidate(sub); // статус мог измениться — сбросить кэш ноды, чтобы reload показал актуальное
    return Results.Ok();
}).RequireAuthorization().DisableAntiforgery();

// ---------------- Админка управления подписками (тонкий клиент /admin/subscriptions) ----------------
// Прокси к ApiService (источник истины) под политикой Admin — браузер админки fetch'ит эти эндпоинты
// в обычном request-контексте (НЕ из рендера Blazor, где исходящий HTTP зависает — грабля #12).
// После изменения сбрасываем кэш entitlement на ноде, чтобы статус подхватился сразу (другие ноды — по TTL).
app.MapGet("/admin/api/subscriptions", async (IHttpClientFactory http, CancellationToken ct) =>
{
    var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
    using var req = new HttpRequestMessage(HttpMethod.Get, "/internal/admin/subscriptions?take=500");
    req.Headers.Add("X-Internal-Key", internalApiKey);
    try
    {
        using var resp = await client.SendAsync(req, ct);
        var rows = resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<List<ChessSchool.Contracts.AdminSubscriptionDto>>(ct)
            : null;
        return Results.Ok(rows ?? []);
    }
    catch { return Results.Ok(Array.Empty<ChessSchool.Contracts.AdminSubscriptionDto>()); }
}).RequireAuthorization("Admin");

app.MapPost("/admin/api/subscriptions/by-email", async (ChessSchool.Contracts.AdminSetByEmailRequest body,
    IHttpClientFactory http, ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
{
    var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
    using var req = new HttpRequestMessage(HttpMethod.Post, "/internal/admin/subscriptions/by-email")
    { Content = System.Net.Http.Json.JsonContent.Create(body) };
    req.Headers.Add("X-Internal-Key", internalApiKey);
    using var resp = await client.SendAsync(req, ct);
    var json = await resp.Content.ReadAsStringAsync(ct);
    if (resp.IsSuccessStatusCode)
    {
        try { ents.Invalidate(System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("sub").GetString()); }
        catch { /* не критично — кэш истечёт по TTL */ }
    }
    return Results.Content(json, "application/json", null, (int)resp.StatusCode);
}).RequireAuthorization("Admin").DisableAntiforgery();

app.MapPost("/admin/api/subscriptions/{sub}", async (string sub, ChessSchool.Contracts.AdminSetSubscriptionRequest body,
    IHttpClientFactory http, ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
{
    var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
    using var req = new HttpRequestMessage(HttpMethod.Post, $"/internal/admin/subscriptions/{Uri.EscapeDataString(sub)}")
    { Content = System.Net.Http.Json.JsonContent.Create(body) };
    req.Headers.Add("X-Internal-Key", internalApiKey);
    using var resp = await client.SendAsync(req, ct);
    var json = await resp.Content.ReadAsStringAsync(ct);
    if (resp.IsSuccessStatusCode) ents.Invalidate(sub);
    return Results.Content(json, "application/json", null, (int)resp.StatusCode);
}).RequireAuthorization("Admin").DisableAntiforgery();

app.MapDelete("/admin/api/subscriptions/{sub}", async (string sub, IHttpClientFactory http,
    ChessSchool.Arena.Services.IPlayerEntitlements ents, CancellationToken ct) =>
{
    var client = http.CreateClient(ChessSchool.Arena.Services.PlayerEntitlements.HttpClientName);
    using var req = new HttpRequestMessage(HttpMethod.Delete, $"/internal/admin/subscriptions/{Uri.EscapeDataString(sub)}");
    req.Headers.Add("X-Internal-Key", internalApiKey);
    using var resp = await client.SendAsync(req, ct);
    if (resp.IsSuccessStatusCode) ents.Invalidate(sub);
    return Results.StatusCode((int)resp.StatusCode);
}).RequireAuthorization("Admin").DisableAntiforgery();

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
