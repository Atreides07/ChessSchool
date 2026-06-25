using ChessSchool.Arena.Components;
using ChessSchool.WebAuth;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Единый вход (SSO) — тот же аккаунт, что и в ChessSchool.
builder.AddChessSchoolSso();

// Co-hosted Orleans silo: турнирные грейны живут прямо в этом процессе.
// Компоненты Blazor вызывают грейны напрямую через IGrainFactory.
// Отдельный кластер силоса арены (изолирован от GameServer по портам и clusterId).
builder.UseOrleans(silo => silo.UseLocalhostClustering(
    siloPort: 11112, gatewayPort: 30001,
    serviceId: "chessschool-arena", clusterId: "chessschool-arena"));

// Внутрипроцессный pub/sub для push-обновлений турниров (грейн → компоненты).
builder.Services.AddSingleton<ChessSchool.Arena.Services.ArenaNotifier>();

// Серверный шахматный движок (Stockfish) для ботов.
builder.Services.AddSingleton<ChessSchool.Arena.Services.IChessEngine, ChessSchool.Arena.Services.StockfishEngine>();

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
