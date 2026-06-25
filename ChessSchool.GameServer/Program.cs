using ChessSchool.GameServer.Hubs;
using ChessSchool.GameServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// --- Ярус состояния: co-hosted Orleans silo. Локально — localhost-кластер без внешних зависимостей.
// В проде кластеризация переключается на Redis/ADO без изменения кода грейнов.
// Отдельный кластер силоса игрового сервера (изолирован от Arena по портам и clusterId).
builder.UseOrleans(silo => silo.UseLocalhostClustering(
    siloPort: 11111, gatewayPort: 30000,
    serviceId: "chessschool-game", clusterId: "chessschool-game"));

// --- Транспортный ярус: SignalR (WebSocket). В проде — Redis backplane между нодами.
builder.Services.AddSignalR();

// Архивация завершённых партий в доменный API.
builder.Services.AddHttpClient<IGameArchiveClient, GameArchiveClient>(c =>
    c.BaseAddress = new("https+http://apiservice"));

// --- Валидация JWT, выпущенных отдельным IdP (общий сервис авторизации) ---
var authority = builder.Configuration["services:auth:https:0"]
    ?? builder.Configuration["services:auth:http:0"];

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.RequireHttpsMetadata = false; // dev: self-signed
        options.MapInboundClaims = false;     // сохраняем claim "sub" как есть
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "chessschool-api",
            ValidateIssuer = true,
            NameClaimType = "name"
        };
        // SignalR передаёт токен в query string при установке WebSocket-соединения.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/gamehub"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// CORS для прямого подключения браузерного SignalR-клиента (тонкий JS-фронт) с другого origin.
// Dev: разрешаем любой origin с credentials. В проде — ограничить списком доменов фронта.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<GameHub>("/gamehub");
app.MapGet("/", () => "ChessSchool GameServer (Orleans + SignalR). Hub: /gamehub");
app.MapDefaultEndpoints();

app.Run();
