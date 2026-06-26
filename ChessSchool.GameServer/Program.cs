using ChessSchool.GameServer.Hubs;
using ChessSchool.GameServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Orleans.Configuration;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// --- Ярус состояния: co-hosted Orleans silo. Изолированный кластер игрового сервера (порты/clusterId).
// Есть Redis → кластеризация через Redis (несколько нод видят общий кластер: грейн партии — единственная
// активация во всём кластере, оба игрока всегда попадают в неё). Нет → localhost-кластер (dev, одна нода).
var redisConn = builder.Configuration.GetRedisConnectionString(builder.Environment);
var siloPort = builder.Configuration.GetValue("Orleans:SiloPort", 11111);
var gatewayPort = builder.Configuration.GetValue("Orleans:GatewayPort", 30000);
builder.UseOrleans(silo =>
{
    if (redisConn is not null)
    {
        silo.UseRedisClustering(o => o.ConfigurationOptions = ConfigurationOptions.Parse(redisConn));
        silo.Configure<ClusterOptions>(o => { o.ClusterId = "chessschool-game"; o.ServiceId = "chessschool-game"; });
        silo.ConfigureEndpoints(siloPort: siloPort, gatewayPort: gatewayPort);
    }
    else
    {
        silo.UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort,
            serviceId: "chessschool-game", clusterId: "chessschool-game");
    }
});

// --- Транспортный ярус: SignalR (WebSocket). Есть Redis → backplane между нодами (сообщение игроку
// долетит, даже если его соединение на другой ноде). Нет → in-proc (dev, одна нода).
var signalr = builder.Services.AddSignalR();
if (redisConn is not null) signalr.AddStackExchangeRedis(redisConn);

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
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment(); // dev: self-signed
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
// Dev: разрешаем любой origin с credentials (порты под Aspire динамические).
// Прод: строго список доменов фронта из конфига Cors:Origins — any-origin + credentials небезопасно
// (любой сайт мог бы дёргать хаб от имени залогиненного пользователя).
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
// Вне Development список origin'ов обязателен: пустой список тихо заблокирует браузерный клиент,
// а any-origin + credentials = дыра. Падаем на старте, если конфиг не задан.
if (!builder.Environment.IsDevelopment() && corsOrigins.Length == 0)
    throw new InvalidOperationException(
        "Cors:Origins не задан вне Development. Укажите домены фронта для браузерного SignalR-клиента.");
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (builder.Environment.IsDevelopment())
    {
        p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }
    else
    {
        p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }
}));

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<GameHub>("/gamehub");
app.MapGet("/", () => "ChessSchool GameServer (Orleans + SignalR). Hub: /gamehub");
app.MapDefaultEndpoints();

app.Run();
