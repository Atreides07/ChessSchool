using System.Net.Http.Json;
using ChessSchool.Arena;
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
builder.Services.AddSingleton(TimeProvider.System); // инжектируемое время в грейн (детерминизм в тестах)

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
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", policy => policy.RequireRole("admin"))
    // Мягкий гейт: чувствительные действия (оплата) — только с подтверждённым e-mail. email_verified —
    // булев OIDC-claim (приходит как "True"/"true"), поэтому читаем через IsEmailVerified (bool.TryParse),
    // а НЕ RequireClaim("email_verified","true") — тот сравнивает ordinal и «True» бы не пропустил.
    .AddPolicy("ConfirmedEmail", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(ctx => ctx.User.IsEmailVerified()));

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

// Онлайн-доски трансляции: опрос «живого» PGN-фида и разбор в позиции. Без авто-редиректа (SSRF —
// редирект мог бы увести во внутреннюю сеть); вызывается из request-контекста minimal-API, не из Blazor.
builder.Services.AddHttpClient(ChessSchool.Arena.Services.BroadcastLive.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ChessArena/1.0 (broadcast live boards)");
})
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    });
builder.Services.AddSingleton<ChessSchool.Arena.Services.BroadcastLive>();

// Подтягивание жеребьёвки по ссылке на турнир chess-results (тонкий клиент /pairings). Без авто-редиректа
// (редирект мог бы увести во внутреннюю сеть — SSRF), хост проверяется в эндпоинте; вызывается из
// request-контекста minimal-API, не из рендера Blazor (грабля #12).
builder.Services.AddHttpClient("PairingFetch", c =>
{
    c.Timeout = TimeSpan.FromSeconds(12);
    c.MaxResponseContentBufferSize = 8 * 1024 * 1024; // защита от гигантских ответов
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ChessArena/1.0 (pairing import)");
})
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    });

// Продуктовая аналитика (PostHog при наличии ключа, иначе no-op).
builder.AddChessSchoolAnalytics();
builder.Services.AddSingleton<ChessSchool.Arena.Services.ArenaTelemetry>(); // формирование arena-событий в одном месте

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

// ---------------- Эндпоинты (вынесены в группы ради читаемости — см. *Endpoints.cs) ----------------
app.MapSeoEndpoints();                     // robots.txt / sitemap.xml
app.MapPremiumEndpoints(internalApiKey);   // премиум/подписки + админ-CRUD подписок
app.MapBroadcastEndpoints();               // трансляции: discovery, онлайн-доски, media, calendar, редиректы
app.MapPairingsEndpoints();                // импорт жеребьёвки chess-results (/pairings)
// Хаб страницы турнира (тонкий браузерный клиент подключается по тому же origin → cookie-auth).
app.MapHub<ChessSchool.Arena.Hubs.ArenaHub>("/arenahub");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
