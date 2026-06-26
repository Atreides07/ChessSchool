using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChessSchool.WebAuth;

/// <summary>
/// Единый вход (SSO) для веб-приложений на стандартном OpenID Connect (authorization code + PKCE)
/// против общего IdP (OpenIddict). Вход один раз — обе системы (ChessSchool и Arena) распознают
/// пользователя по общей сессии IdP. Access-токен кладётся в claim сессии для использования в SignalR.
/// </summary>
public static class SsoExtensions
{
    public const string AccessTokenClaim = "access_token";

    public static void AddChessSchoolSso(this WebApplicationBuilder builder)
    {
        var clientId = builder.Configuration["Sso:ClientId"] ?? "app";
        var authority = builder.Configuration["services:auth:https:0"]
            ?? builder.Configuration["services:auth:http:0"];

        // Общий DataProtection-keyring (Redis при наличии): cookie/тикеты одной ноды читает любая другая.
        builder.AddChessSchoolDataProtection();

        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(o =>
        {
            o.Cookie.Name = $"app_{clientId}";
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
        })
        .AddOpenIdConnect(o =>
        {
            o.Authority = authority;
            o.ClientId = clientId;
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();   // dev: self-signed
            o.ResponseType = "code";
            o.UsePkce = true;
            o.SaveTokens = true;
            o.GetClaimsFromUserInfoEndpoint = true;
            o.MapInboundClaims = false;
            o.TokenValidationParameters.NameClaimType = "name";

            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
            o.Scope.Add("chess.api");
            o.Scope.Add("offline_access"); // нужен refresh_token для обновления access-токена

            // Кладём токены в claims, чтобы Blazor-цепь (server-side) могла брать access-токен
            // для SignalR и обновлять его по refresh_token при истечении.
            o.Events = new OpenIdConnectEvents
            {
                OnTokenValidated = ctx =>
                {
                    var r = ctx.TokenEndpointResponse;
                    if (r?.AccessToken is { } at && ctx.Principal?.Identity is ClaimsIdentity identity)
                    {
                        identity.AddClaim(new Claim(AccessTokenClaim, at));
                        if (!string.IsNullOrEmpty(r.RefreshToken))
                            identity.AddClaim(new Claim("refresh_token", r.RefreshToken));
                        if (int.TryParse(r.ExpiresIn, out var exp))
                            identity.AddClaim(new Claim("token_expires_at", DateTimeOffset.UtcNow.AddSeconds(exp).ToString("o")));
                    }
                    return Task.CompletedTask;
                }
            };
        });

        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();

        // Серверное хранилище тикетов: в cookie остаётся только ключ, а большие OIDC-токены
        // (access/id/refresh) хранятся на сервере. Иначе cookie раздувается и Kestrel отвечает HTTP 431.
        // Есть Redis → общий распределённый стор (тикет, выданный одной нодой, читает любая другая —
        // обязательно для мультисервера за балансировщиком). Нет → файловый стор (dev/одна нода):
        // переживает перезапуск сервиса, пользователь остаётся авторизованным.
        var redis = builder.Configuration.GetRedisConnectionString();
        if (redis is not null)
        {
            builder.Services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = $"chessschool:tickets:{clientId}:";
            });
            builder.Services.AddSingleton<ITicketStore, DistributedCacheTicketStore>();
        }
        else
        {
            var ticketDir = Path.Combine(builder.Environment.ContentRootPath, "keys", "auth-tickets");
            builder.Services.AddSingleton<ITicketStore>(sp =>
                new FileSystemTicketStore(ticketDir, sp.GetRequiredService<IDataProtectionProvider>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<FileSystemTicketStore>()));
        }
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);
    }

    public static void MapSsoEndpoints(this WebApplication app)
    {
        // Вход: challenge OIDC. Если SSO-сессия IdP уже есть — вход проходит прозрачно, без формы.
        app.MapGet("/signin", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = returnUrl ?? "/" },
                [OpenIdConnectDefaults.AuthenticationScheme]));

        app.MapGet("/signout", () =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
    }
}

/// <summary>
/// Хранит тикет аутентификации на сервере в файле; в cookie кладётся только короткий ключ.
/// Это держит cookie маленькой даже при больших OIDC-токенах И переживает перезапуск сервиса
/// (in-memory-вариант терял тикеты при рестарте → пользователь «выпадал» в неавторизованного).
/// Тикет на диске шифруется DataProtection. Для прод-многонодового сценария заменить на
/// распределённый кэш (Redis) — общий стор для всех нод.
/// </summary>
public sealed class FileSystemTicketStore : ITicketStore
{
    private static readonly Regex KeyPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);
    private readonly string _dir;
    private readonly IDataProtector _protector;
    private readonly ILogger _log;

    public FileSystemTicketStore(string dir, IDataProtectionProvider dataProtection, ILogger log)
    {
        _dir = dir;
        _log = log;
        Directory.CreateDirectory(_dir);
        _protector = dataProtection.CreateProtector("ChessSchool.WebAuth.FileSystemTicketStore.v1");
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        if (!KeyPattern.IsMatch(key)) return;
        var bytes = TicketSerializer.Default.Serialize(ticket);
        await File.WriteAllBytesAsync(PathFor(key), _protector.Protect(bytes));
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (!KeyPattern.IsMatch(key)) return null; // защита от path traversal
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = _protector.Unprotect(await File.ReadAllBytesAsync(path));
            var ticket = TicketSerializer.Default.Deserialize(bytes);
            if (ticket?.Properties.ExpiresUtc is { } exp && exp < DateTimeOffset.UtcNow)
            {
                await RemoveAsync(key);
                return null;
            }
            return ticket;
        }
        catch (Exception ex)
        {
            // Повреждённый/нерасшифровываемый тикет (например, сменились DataProtection-ключи) →
            // ведём себя как при отсутствии, но логируем для диагностики.
            _log.LogWarning(ex, "Не удалось прочитать тикет аутентификации (ключ {Key}).", key);
            return null;
        }
    }

    public Task RemoveAsync(string key)
    {
        if (KeyPattern.IsMatch(key))
        {
            var path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string PathFor(string key) => Path.Combine(_dir, key + ".tkt");
}

/// <summary>
/// Распределённое хранилище тикетов поверх IDistributedCache (Redis). Тикет, выданный одной нодой,
/// доступен всем нодам кластера — обязательно для мультисервера за балансировщиком. Значение шифруется
/// DataProtection (общий keyring). TTL синхронизируется со сроком жизни сессии.
/// </summary>
public sealed class DistributedCacheTicketStore(IDistributedCache cache, IDataProtectionProvider dataProtection)
    : ITicketStore
{
    private const string Prefix = "ticket:";
    private readonly IDataProtector _protector =
        dataProtection.CreateProtector("ChessSchool.WebAuth.DistributedCacheTicketStore.v1");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(8)
        };
        var bytes = _protector.Protect(TicketSerializer.Default.Serialize(ticket));
        await cache.SetAsync(Prefix + key, bytes, options);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var bytes = await cache.GetAsync(Prefix + key);
        if (bytes is null) return null;
        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(bytes));
        }
        catch
        {
            return null; // повреждённый/нерасшифровываемый тикет → как будто его нет
        }
    }

    public Task RemoveAsync(string key) => cache.RemoveAsync(Prefix + key);
}
