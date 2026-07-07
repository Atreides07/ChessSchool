using System.Security.Claims;
using System.Threading.RateLimiting;
using ChessSchool.Auth;
using ChessSchool.Auth.Data;
using ChessSchool.Auth.Email;
using ChessSchool.Contracts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// За обратным прокси (Aspire/ingress) доверяем forwarded-заголовкам, чтобы issuer/эндпоинты OIDC
// строились по внешнему https-хосту (иначе токены/discovery будут с внутренним адресом).
builder.AddChessSchoolForwardedHeaders();

// Локализация страницы входа RU/EN. Cookie языка живёт на хосте веб-приложения и не приходит на
// отдельный домен IdP, поэтому язык определяется по ui_locales (OIDC), затем Accept-Language/?culture.
builder.AddChessSchoolLocalization();

// Уважаем OIDC-параметр ui_locales (язык приложения): из запроса /connect/authorize или из ReturnUrl
// (когда authorize редиректит на /account/login). Проверяется раньше прочих провайдеров.
builder.Services.Configure<RequestLocalizationOptions>(o =>
    o.RequestCultureProviders.Insert(0, new CustomRequestCultureProvider(ctx =>
    {
        static string? Pick(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            foreach (var tag in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var two = tag.Split('-')[0].ToLowerInvariant();
                if (two is "ru" or "en") return two;
            }
            return null;
        }

        var q = ctx.Request.Query;
        var c = Pick(q["ui_locales"]);
        if (c is null)
        {
            var ret = q["ReturnUrl"].ToString();
            if (string.IsNullOrEmpty(ret)) ret = q["return"].ToString();
            var qi = ret.IndexOf('?');
            if (qi >= 0)
            {
                var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(ret[qi..]);
                if (parsed.TryGetValue("ui_locales", out var v)) c = Pick(v);
                else if (parsed.TryGetValue("culture", out var cv)) c = Pick(cv);
            }
        }
        return Task.FromResult<ProviderCultureResult?>(c is null ? null : new ProviderCultureResult(c));
    })));

// Общий DataProtection-keyring (Redis при наличии): cookie-сессия IdP расшифровывается любой нодой —
// без этого при нескольких нодах IdP вход «прыгал» бы и логин ломался.
builder.AddChessSchoolDataProtection();

// Readiness-проверки зависимостей (в /health, не в /alive). Без строк подключения — пропускаем.
if (builder.Configuration.GetConnectionString("authdb") is { Length: > 0 } authConn)
    builder.Services.AddHealthChecks().AddNpgSql(authConn, name: "postgres");
if (builder.Configuration.GetRedisConnectionString() is { } authRedis)
    builder.Services.AddHealthChecks().AddRedis(authRedis, name: "redis");

// БД — PostgreSQL (connection string инжектит Aspire по ссылке на ресурс "auth").
// Хранилище OpenIddict — в том же контексте.
builder.Services.AddDbContext<AuthDbContext>(o =>
{
    o.UseNpgsql(builder.Configuration.GetConnectionString("authdb"));
    o.UseOpenIddict(); // регистрирует сущности OpenIddict в модели
});

builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddProblemDetails();

// Почта: подтверждение e-mail при регистрации. Есть SMTP-хост (mailpit локально / реальный SMTP в проде) →
// шлём по-настоящему; нет → лог-фолбэк (dev без почтового сервера/тесты). Одноразовые токены — EmailTokenService.
var emailOptions = EmailOptions.FromConfig(builder.Configuration);
builder.Services.AddSingleton(emailOptions);
if (!string.IsNullOrWhiteSpace(emailOptions.Host))
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();
builder.Services.AddScoped<EmailTokenService>();
builder.Services.AddScoped<AuthAudit>(); // аудит auth-событий (наблюдаемость/детект аномалий) в общий стор
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(AuthMetrics.MeterName)); // метрики auth для алертинга
builder.Services.AddScoped<MfaService>(); // двухфакторка (TOTP): секрет шифруется DataProtection, recovery-коды

// Политика паролей (NIST): минимальная длина из конфига (дефолт 8), проверка утечек по HIBP (k-anonymity).
// CheckPwned выключается в тестах (не ходить в сеть). HttpClient к api.pwnedpasswords.com — короткий таймаут.
var minPasswordLength = builder.Configuration.GetValue("Auth:Password:MinLength", 8);
var checkPwned = builder.Configuration.GetValue("Auth:Password:CheckPwned", true);
builder.Services.AddHttpClient(PwnedPasswordChecker.HttpClientName, c =>
{
    c.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
    c.Timeout = TimeSpan.FromSeconds(5);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("ChessSchool-Auth/1.0 (pwned-check)");
});
builder.Services.AddSingleton<IPwnedPasswordChecker, PwnedPasswordChecker>();

// Rate-limiting: против перебора пароля (login/confirm) и email-бомбинга (register/resend/change-email —
// они шлют письма). Лимиты конфигурируемы (тесты поднимают их, чтобы не мешать). Партиция — по IP клиента
// (за прокси корректен благодаря forwarded-заголовкам). ВАЖНО: лимитер in-memory ПОнодовый — при нескольких
// нодах суммарный лимит = N×порог; распределённый вариант (Redis) — follow-up (см. принцип мультисерверности).
var rlAuthPermit = builder.Configuration.GetValue("RateLimit:Auth:Permit", 20);
var rlAuthWindow = builder.Configuration.GetValue("RateLimit:Auth:WindowMinutes", 5);
var rlEmailPermit = builder.Configuration.GetValue("RateLimit:Email:Permit", 5);
var rlEmailWindow = builder.Configuration.GetValue("RateLimit:Email:WindowMinutes", 15);
// Есть Redis → распределённый лимитер (общий счётчик на все ноды, лимит не размножается на реплики);
// нет (dev/одна нода) → in-memory fixed-window. Мультиплексор один на процесс (как в DataProtection).
var rlRedis = builder.Configuration.GetRedisConnectionString() is { } rlConn
    ? StackExchange.Redis.ConnectionMultiplexer.Connect(rlConn) : null;
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    RateLimitPartition<string> Partition(HttpContext ctx, string policy, int permit, int windowMin)
    {
        var window = TimeSpan.FromMinutes(windowMin);
        var ip = Ip(ctx);
        if (rlRedis is null)
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window });
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<RedisFixedWindowRateLimiter>();
        return RateLimitPartition.Get(ip, key => new RedisFixedWindowRateLimiter(rlRedis, $"rl:{policy}:{key}", permit, window, log));
    }

    o.AddPolicy("auth", ctx => Partition(ctx, "auth", rlAuthPermit, rlAuthWindow));
    o.AddPolicy("email-send", ctx => Partition(ctx, "email", rlEmailPermit, rlEmailWindow));
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = ((int)TimeSpan.FromMinutes(rlAuthWindow).TotalSeconds).ToString();
        AuthMetrics.RecordRateLimited(ctx.HttpContext.Request.Path); // сигнал перебора/бомбинга для алертинга
        return ValueTask.CompletedTask;
    };
});

// Cookie-сессия IdP: единый вход (страница логина одна для всех приложений).
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "idp";
})
.AddCookie("idp", o =>
{
    o.Cookie.Name = "idp_sso";
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.LoginPath = "/account/login";
    o.ReturnUrlParameter = "return";
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    // Security-stamp: сверяем метку из cookie с текущей в БД. Не совпала (пароль сменили на др. устройстве
    // или пользователя удалили) → отклоняем сессию и разлогиниваем. Проверка не на каждый запрос, а с
    // интервалом (баланс «мгновенность vs нагрузка на БД») — настраивается Auth:SecurityStamp:ValidateMinutes.
    o.Events.OnValidatePrincipal = async context =>
    {
        var interval = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()
            .GetValue("Auth:SecurityStamp:ValidateMinutes", 5);
        var issued = context.Properties.IssuedUtc;
        if (issued is not null && DateTimeOffset.UtcNow - issued.Value < TimeSpan.FromMinutes(interval))
            return; // рано перепроверять — доверяем cookie до следующего интервала

        var sub = context.Principal?.FindFirst("sub")?.Value;
        var stamp = context.Principal?.FindFirst("sstamp")?.Value;
        if (!Guid.TryParse(sub, out var uid))
            return; // старые cookie без sub не трогаем (обратная совместимость)

        var db = context.HttpContext.RequestServices.GetRequiredService<AuthDbContext>();
        var current = await db.Users.Where(u => u.Id == uid).Select(u => u.SecurityStamp).FirstOrDefaultAsync();
        if (current is null || current != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync("idp");
            return;
        }
        context.ShouldRenew = true; // продлеваем и обновляем IssuedUtc, чтобы интервал считался заново
    };
});
builder.Services.AddAuthorization();

// ---- OpenIddict: полноценный OIDC-сервер (authorization code + PKCE, refresh) ----
builder.Services.AddOpenIddict()
    .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
    .AddServer(o =>
    {
        o.SetAuthorizationEndpointUris("connect/authorize")
         .SetTokenEndpointUris("connect/token")
         .SetUserInfoEndpointUris("connect/userinfo")
         .SetEndSessionEndpointUris("connect/logout");

        o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
        o.RequireProofKeyForCodeExchange();
        o.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, "chess.api");

        // Dev-сертификаты. Access-токен НЕ шифруем — чтобы ресурс-серверы валидировали JWT по JWKS.
        // Сертификаты подписи/шифрования токенов. Dev — эфемерные (удобно локально). Прод — постоянные
        // X.509 из секретов (иначе ключи разъезжаются между нодами/рестартами → токены/JWKS невалидны).
        if (builder.Environment.IsDevelopment())
        {
            o.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
        }
        else
        {
            o.AddSigningCertificate(Certificates.LoadFromConfig(builder.Configuration, "OpenIddict:SigningCertificate"))
             .AddEncryptionCertificate(Certificates.LoadFromConfig(builder.Configuration, "OpenIddict:EncryptionCertificate"));
        }
        o.DisableAccessTokenEncryption();

        var aspnet = o.UseAspNetCore()
         .EnableAuthorizationEndpointPassthrough()
         .EnableTokenEndpointPassthrough()
         .EnableUserInfoEndpointPassthrough()
         .EnableEndSessionEndpointPassthrough();

        // В Development разрешаем HTTP (локальная разработка/интеграционные тесты без TLS).
        // В проде требование HTTPS-транспорта остаётся (Aspire отдаёт https-эндпоинты).
        if (builder.Environment.IsDevelopment())
            aspnet.DisableTransportSecurityRequirement();
    })
    .AddValidation(o =>
    {
        o.UseLocalServer();
        o.UseAspNetCore();
    });

builder.Services.AddHostedService<ClientSeeder>();

var app = builder.Build();

// Секрет server-to-server вызовов — резолвим на старте (вне Development падаем, если не задан).
var internalKey = builder.Configuration.ResolveInternalApiKey(builder.Environment);

// Фиктивный хэш для constant-time логина: при отсутствии пользователя всё равно выполняем VerifyHashedPassword,
// чтобы время ответа не выдавало существование аккаунта (анти-энумерация по таймингу).
var dummyPasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), "constant-time-dummy");

// Кто админ (источник истины — IdP): список e-mail из Admin:Emails, по умолчанию — akhmed@outlook.com.
// Для этих пользователей в токен уходит claim role=admin; потребители гейтят админку по роли.
var adminEmails = AdminRoles.Resolve(builder.Configuration["Admin:Emails"]);
// Обязательная MFA для админов: без включённой 2FA админ не получает токен приложения (гейт в authorize)
// и на входе форсится в настройку. Отключаемо конфигом (напр. для локальной отладки).
var requireMfaForAdmins = builder.Configuration.GetValue("Auth:Mfa:RequiredForAdmins", true);

// Применение схемы. В проде миграции выкатываются ОТДЕЛЬНЫМ шагом (тот же образ с аргументом `migrate`
// как k8s Job), а боевые реплики стартуют без авто-миграции (нет гонки нескольких реплик за первую
// миграцию). Флаг Database:MigrateAtStartup (по умолчанию = Development) и режим `migrate` это включают.
var migrateRequested = args.Contains("migrate");
var migrateAtStartup = builder.Configuration.GetValue("Database:MigrateAtStartup", builder.Environment.IsDevelopment());
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    if (!db.Database.IsNpgsql()) db.Database.EnsureCreated();          // InMemory (тесты)
    else if (migrateRequested || migrateAtStartup) db.Database.Migrate();
}
if (migrateRequested) return; // режим миграции: схему применили — выходим (job завершён)

app.UseForwardedHeaders(); // схема/хост из X-Forwarded-* до построения issuer/redirect
app.UseChessSchoolLocalization(); // культура страницы входа (Accept-Language/?culture)
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // после аутентификации; политики навешены на account-эндпоинты ниже

// ---------------- Эндпоинты (вынесены в группы ради читаемости — см. *Endpoints.cs) ----------------
// Захваченную ранее конфигурацию прокидываем явно: логика/HTML — в AccountFlow/AccountPages.
var authConfig = new AuthConfig(minPasswordLength, checkPwned, dummyPasswordHash, adminEmails, requireMfaForAdmins);
app.MapAccountEndpoints(authConfig);   // вход/регистрация/подтверждение/смена e-mail/сброс пароля
app.MapMfaEndpoints(authConfig);       // 2FA: настройка/включение/отключение/второй фактор
app.MapOidcEndpoints(authConfig);      // OpenIddict: authorize/token/userinfo/logout
app.MapInternalUserEndpoints(internalKey); // server-to-server резолв email→sub / sub→профиль

app.MapDefaultEndpoints();
app.Run();
