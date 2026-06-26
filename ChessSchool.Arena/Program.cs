using ChessSchool.Arena.Components;
using ChessSchool.WebAuth;
using Orleans.Configuration;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Единый вход (SSO) — тот же аккаунт, что и в ChessSchool.
builder.AddChessSchoolSso();

// Co-hosted Orleans silo: турнирные грейны живут прямо в этом процессе.
// Компоненты Blazor вызывают грейны напрямую через IGrainFactory.
// Отдельный кластер силоса арены (изолирован от GameServer по портам и clusterId).
// Есть Redis → кластеризация и хранилище турниров в Redis: состояние (таблица/история/мета)
// переживает перезапуск и масштабирование силосов, грейн турнира — единственная активация в кластере.
// Нет → localhost-кластер + in-memory storage (dev, одна нода).
var redisConn = builder.Configuration.GetRedisConnectionString();
builder.UseOrleans(silo =>
{
    if (redisConn is not null)
    {
        silo.UseRedisClustering(o => o.ConfigurationOptions = ConfigurationOptions.Parse(redisConn));
        silo.Configure<ClusterOptions>(o => { o.ClusterId = "chessschool-arena"; o.ServiceId = "chessschool-arena"; });
        silo.ConfigureEndpoints(siloPort: 11112, gatewayPort: 30001);
        silo.AddRedisGrainStorage("arena", o => o.ConfigurationOptions = ConfigurationOptions.Parse(redisConn));
        // Reminders в Redis: грейн турнира «воскресает» на любой ноде даже при потере текущей.
        silo.UseRedisReminderService(o => o.ConfigurationOptions = ConfigurationOptions.Parse(redisConn));
    }
    else
    {
        silo.UseLocalhostClustering(
            siloPort: 11112, gatewayPort: 30001,
            serviceId: "chessschool-arena", clusterId: "chessschool-arena");
        silo.AddMemoryGrainStorage("arena");
    }
});

// Рантайм-переключатели грейна (reminders доступны только при настроенном Redis-сервисе).
builder.Services.AddSingleton(new ChessSchool.Arena.Services.ArenaRuntimeOptions(RemindersEnabled: redisConn is not null));

// Внутрипроцессный pub/sub для push-обновлений турниров (грейн → компоненты).
builder.Services.AddSingleton<ChessSchool.Arena.Services.ArenaNotifier>();

// Серверный шахматный движок (Stockfish) для ботов.
builder.Services.AddSingleton<ChessSchool.Arena.Services.IChessEngine, ChessSchool.Arena.Services.StockfishEngine>();

// Доступ к cookie запроса (запоминание выбранного вида расписания при SSR-навигации).
builder.Services.AddHttpContextAccessor();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
