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

// Внутрипроцессный pub/sub для push-обновлений турниров (грейн → компоненты).
builder.Services.AddSingleton<ChessSchool.Arena.Services.ArenaNotifier>();

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

// Продуктовая аналитика (PostHog при наличии ключа, иначе no-op).
builder.AddChessSchoolAnalytics();

// Доступ к cookie запроса (запоминание выбранного вида расписания при SSR-навигации).
builder.Services.AddHttpContextAccessor();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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
app.MapGet("/sitemap.xml", async (HttpRequest r, ChessSchool.Arena.Services.BroadcastsCatalog catalog) =>
{
    var b = $"{r.Scheme}://{r.Host}";
    var paths = new List<string> { "", "broadcasts" };
    // Только видимые трансляции — скрытые не должны попадать в индекс.
    paths.AddRange((await catalog.PublicAsync()).Select(m => $"broadcasts/{m.Slug}"));
    var locs = string.Join("\n", paths.Select(u => $"  <url><loc>{b}/{u}</loc></url>"));
    return Results.Text(
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n{locs}\n</urlset>\n",
        "application/xml");
});

// Раздел переименован «Турниры» → «Трансляции»: старые пути 301-редиректятся на /broadcasts (без битых ссылок).
app.MapGet("/majors", () => Results.Redirect("/broadcasts", permanent: true));
app.MapGet("/majors/{slug}", (string slug) => Results.Redirect($"/broadcasts/{slug}", permanent: true));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
