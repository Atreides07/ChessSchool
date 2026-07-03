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

// ---------------- Страница входа / регистрации (cookie-сессия IdP) ----------------
app.MapGet("/account/login", (string? @return, string? error, string? mode, string? email) =>
    Results.Content(LoginPage(@return ?? "/", error, mode == "register", mode == "sent", email, minPasswordLength),
        "text/html; charset=utf-8"));

app.MapPost("/account/login", async (HttpContext ctx, AuthDbContext db, IPasswordHasher<AppUser> hasher, AuthAudit audit, IEmailSender emailSender) =>
{
    var form = await ctx.Request.ReadFormAsync();
    string email = form["email"].ToString().Trim().ToLowerInvariant();
    string ret = form["return"].ToString();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    if (user is null)
    {
        hasher.VerifyHashedPassword(new AppUser(), dummyPasswordHash, form["password"]!); // выравниваем тайминг
        await audit.LogAsync(ctx, AuthEventType.LoginFailure, email, detail: "no-user");
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1");
    }
    if (hasher.VerifyHashedPassword(user, user.PasswordHash, form["password"]!) == PasswordVerificationResult.Failed)
    {
        await audit.LogAsync(ctx, AuthEventType.LoginFailure, email, user.Id, "bad-password");
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1");
    }

    // Вход с нового устройства? Проверяем ДО записи текущего события (иначе текущий IP сразу «известен»).
    var ip = ctx.Connection.RemoteIpAddress?.ToString();
    var hadPriorLogins = await db.AuthEvents.AnyAsync(e => e.UserId == user.Id && e.Type == AuthEventType.LoginSuccess);
    var knownIp = ip is not null &&
        await db.AuthEvents.AnyAsync(e => e.UserId == user.Id && e.Type == AuthEventType.LoginSuccess && e.Ip == ip);

    // Мягкий гейт: пускаем и с неподтверждённым e-mail (claim email_verified=false едет в токен;
    // чувствительные действия приложения закрывают сами). Подтверждение — nudge-баннером в приложении.
    await SignInCookieAsync(ctx, user);
    await audit.LogAsync(ctx, AuthEventType.LoginSuccess, email, user.Id);

    // Уведомляем владельца о входе с ранее не виденного IP (не на первом входе — тогда «прежних» нет).
    if (hadPriorLogins && !knownIp)
    {
        var (subject, html) = EmailTemplates.NewSignIn(user.DisplayName, ip, IsEnCulture());
        await emailSender.SendAsync(user.Email, subject, html);
        await audit.LogAsync(ctx, AuthEventType.NewDeviceLogin, email, user.Id, detail: ip);
    }
    return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
}).RequireRateLimiting("auth"); // защита от перебора пароля

app.MapPost("/account/register", async (HttpContext ctx, AuthDbContext db, IPasswordHasher<AppUser> hasher,
    EmailTokenService tokens, IEmailSender email, IPwnedPasswordChecker pwned, AuthAudit audit, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync();
    string em = form["email"].ToString().Trim().ToLowerInvariant();
    string ret = form["return"].ToString();
    string password = form["password"]!;
    if (string.IsNullOrWhiteSpace(em) || !em.Contains('@'))
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1&mode=register");
    if (!PasswordPolicy.IsAcceptable(password, minPasswordLength, out _)) // NIST: решает длина, без композиции
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=weak&mode=register");

    var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == em);
    if (existing is not null)
    {
        // Уже подтверждён → e-mail занят, ведём на вход. Не подтверждён → переотправляем письмо.
        if (existing.EmailConfirmed)
            return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=exists&mode=register");
        await SendConfirmationEmailAsync(ctx, tokens, email, existing, ret);
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&mode=sent&email={Uri.EscapeDataString(em)}");
    }

    // Пароль не должен фигурировать в известных утечках (HIBP, k-anonymity). Недоступность HIBP → не блокируем.
    if (checkPwned && await pwned.IsPwnedAsync(password, ct))
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=pwned&mode=register");

    // Регистрация: создаём НЕподтверждённого, шлём письмо и СРАЗУ пускаем (мягкий гейт) — ценность
    // доступна немедленно, подтверждение просим баннером; чувствительное закрыто до email_verified=true.
    var user = new AppUser { Email = em, DisplayName = form["name"].ToString() };
    user.PasswordHash = hasher.HashPassword(user, password);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    await SendConfirmationEmailAsync(ctx, tokens, email, user, ret);
    await SignInCookieAsync(ctx, user);
    await audit.LogAsync(ctx, AuthEventType.Register, em, user.Id);
    return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
}).RequireRateLimiting("email-send"); // регистрация шлёт письмо → анти-бомбинг

// ---------------- Подтверждение e-mail по ссылке из письма ----------------
app.MapGet("/account/confirm", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens,
    AuthAudit audit, string? token, string? @return) =>
{
    var userId = await tokens.ConsumeAsync(token, EmailTokenPurpose.ConfirmEmail);
    var user = userId is { } id ? await db.Users.FindAsync(id) : null;
    if (user is null) // ссылка недействительна/устарела/использована → на вход с предложением новой ссылки
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(@return ?? "")}&error=badtoken");

    if (!user.EmailConfirmed) { user.EmailConfirmed = true; await db.SaveChangesAsync(); }
    // Подтвердил → сразу вход и возврат туда, откуда пришёл (обычно /connect/authorize → назад в приложение).
    await SignInCookieAsync(ctx, user);
    await audit.LogAsync(ctx, AuthEventType.EmailConfirmed, user.Email, user.Id);
    return Results.Redirect(string.IsNullOrEmpty(@return) ? "/" : @return);
}).RequireRateLimiting("auth"); // защита от перебора токена подтверждения

// ---------------- Переотправка письма подтверждения (нейтральный ответ) ----------------
app.MapPost("/account/resend", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens, IEmailSender email, AuthAudit audit) =>
{
    var form = await ctx.Request.ReadFormAsync();
    string em = form["email"].ToString().Trim().ToLowerInvariant();
    string ret = form["return"].ToString();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == em);
    if (user is not null && !user.EmailConfirmed)
    {
        await SendConfirmationEmailAsync(ctx, tokens, email, user, ret);
        await audit.LogAsync(ctx, AuthEventType.ConfirmationResent, em, user.Id);
    }
    // Нейтрально: всегда «письмо отправлено» (не раскрываем, есть ли такой аккаунт).
    return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&mode=sent&email={Uri.EscapeDataString(em)}");
}).RequireRateLimiting("email-send"); // анти-бомбинг переотправкой

// ---------------- Управление e-mail: смена адреса ДО подтверждения (исправить опечатку) ----------------
// Требует входа (мягкий гейт → пользователь уже внутри). Подтверждённый адрес здесь не меняем.
app.MapGet("/account/email", async (HttpContext ctx, AuthDbContext db, string? @return, string? error) =>
{
    var auth = await ctx.AuthenticateAsync("idp");
    var user = Guid.TryParse(auth.Principal?.FindFirst("sub")?.Value, out var id) ? await db.Users.FindAsync(id) : null;
    if (user is null) return Results.Redirect($"/account/login?return={Uri.EscapeDataString(@return ?? "/")}");
    return Results.Content(AccountEmailPage(user.Email, user.EmailConfirmed, user.PendingEmail, @return ?? "/", error), "text/html; charset=utf-8");
});

app.MapPost("/account/change-email", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens, IEmailSender email, AuthAudit audit) =>
{
    var auth = await ctx.AuthenticateAsync("idp");
    var user = Guid.TryParse(auth.Principal?.FindFirst("sub")?.Value, out var id) ? await db.Users.FindAsync(id) : null;
    var form = await ctx.Request.ReadFormAsync();
    string ret = form["return"].ToString();
    if (user is null) return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}");

    string newEmail = form["email"].ToString().Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(newEmail) || !newEmail.Contains('@'))
        return Results.Redirect($"/account/email?return={Uri.EscapeDataString(ret)}&error=invalid");
    if (await db.Users.AnyAsync(u => u.Email == newEmail && u.Id != user.Id))
        return Results.Redirect($"/account/email?return={Uri.EscapeDataString(ret)}&error=taken");

    if (user.EmailConfirmed)
    {
        // ПОДТВЕРЖДЁННЫЙ адрес: verify-new-before-switch — основной e-mail не трогаем, пока владение новым
        // не доказано переходом по ссылке. Ссылка уходит на НОВЫЙ адрес, уведомление — на СТАРЫЙ (OWASP).
        if (newEmail == user.Email)
            return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret); // адрес не изменился
        user.PendingEmail = newEmail;
        await db.SaveChangesAsync();

        var raw = await tokens.CreateAsync(user.Id, EmailTokenPurpose.ChangeEmail, EmailTokenService.ConfirmLifetime);
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var link = $"{baseUrl}/account/confirm-email-change?token={Uri.EscapeDataString(raw)}&return={Uri.EscapeDataString(ret)}";
        var (subject, html) = EmailTemplates.ConfirmEmailChange(user.DisplayName, link, newEmail, IsEnCulture());
        await email.SendAsync(newEmail, subject, html);                        // подтверждение — на новый адрес
        var (nSub, nHtml) = EmailTemplates.EmailChangeRequested(user.DisplayName, newEmail, IsEnCulture());
        await email.SendAsync(user.Email, nSub, nHtml);                        // уведомление — на старый адрес
        await audit.LogAsync(ctx, AuthEventType.EmailChanged, user.Email, user.Id, detail: $"requested:{newEmail}");
        return Results.Redirect($"/account/login?mode=sent&email={Uri.EscapeDataString(newEmail)}&return={Uri.EscapeDataString(ret)}");
    }

    // НЕподтверждённый адрес: исправление опечатки — меняем сразу и шлём подтверждение на новый.
    var oldEmail = user.Email;
    user.Email = newEmail;
    await db.SaveChangesAsync();
    await audit.LogAsync(ctx, AuthEventType.EmailChanged, newEmail, user.Id, detail: $"from:{oldEmail}");
    await SignInCookieAsync(ctx, user);                                   // обновляем e-mail в cookie
    await SendConfirmationEmailAsync(ctx, tokens, email, user, ret);      // письмо на новый адрес
    return Results.Redirect($"/account/login?mode=sent&email={Uri.EscapeDataString(newEmail)}&return={Uri.EscapeDataString(ret)}");
}).RequireRateLimiting("email-send"); // анти-бомбинг сменой адреса

// ---------------- Смена ПОДТВЕРЖДЁННОГО e-mail: подтверждение нового адреса ----------------
app.MapGet("/account/confirm-email-change", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens,
    AuthAudit audit, string? token, string? @return) =>
{
    var userId = await tokens.ConsumeAsync(token, EmailTokenPurpose.ChangeEmail);
    var user = userId is { } id ? await db.Users.FindAsync(id) : null;
    if (user is null || string.IsNullOrEmpty(user.PendingEmail))
        return Results.Redirect($"/account/login?return={Uri.EscapeDataString(@return ?? "")}&error=badtoken");

    var newEmail = user.PendingEmail;
    // Пока ссылка «летела», адрес мог занять кто-то другой — тогда не переключаем.
    if (await db.Users.AnyAsync(u => u.Email == newEmail && u.Id != user.Id))
    {
        user.PendingEmail = null;
        await db.SaveChangesAsync();
        return Results.Redirect($"/account/email?return={Uri.EscapeDataString(@return ?? "/")}&error=taken");
    }

    var oldEmail = user.Email;
    user.Email = newEmail;
    user.PendingEmail = null;
    user.EmailConfirmed = true;
    user.SecurityStamp = Guid.NewGuid().ToString("N"); // смена идентичности → инвалидируем прочие сессии
    await db.SaveChangesAsync();
    await audit.LogAsync(ctx, AuthEventType.EmailChanged, newEmail, user.Id, detail: $"confirmed-from:{oldEmail}");
    await SignInCookieAsync(ctx, user); // обновляем e-mail и метку в текущей cookie
    return Results.Redirect(string.IsNullOrEmpty(@return) ? "/" : @return);
}).RequireRateLimiting("auth"); // защита от перебора токена смены адреса

// ---------------- Сброс пароля: запрос ссылки (нейтральный ответ) ----------------
app.MapGet("/account/forgot", (string? @return, bool sent, string? email, string? error) =>
    Results.Content(ForgotPasswordPage(@return ?? "/", sent, email, error), "text/html; charset=utf-8"));

app.MapPost("/account/forgot", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens, IEmailSender email, AuthAudit audit) =>
{
    var form = await ctx.Request.ReadFormAsync();
    string em = form["email"].ToString().Trim().ToLowerInvariant();
    string ret = form["return"].ToString();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == em);
    if (user is not null)
    {
        var raw = await tokens.CreateAsync(user.Id, EmailTokenPurpose.ResetPassword, EmailTokenService.ResetLifetime);
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var link = $"{baseUrl}/account/reset?token={Uri.EscapeDataString(raw)}&return={Uri.EscapeDataString(ret)}";
        var (subject, html) = EmailTemplates.ResetPassword(user.DisplayName, link, IsEnCulture());
        await email.SendAsync(user.Email, subject, html);
        await audit.LogAsync(ctx, AuthEventType.PasswordResetRequested, em, user.Id);
    }
    // Нейтрально: всегда «письмо отправлено, если такой аккаунт есть» — не раскрываем существование почты.
    return Results.Redirect($"/account/forgot?sent=true&return={Uri.EscapeDataString(ret)}&email={Uri.EscapeDataString(em)}");
}).RequireRateLimiting("email-send"); // анти-бомбинг письмами сброса

// ---------------- Сброс пароля: форма нового пароля по ссылке из письма ----------------
app.MapGet("/account/reset", (string? token, string? @return, string? error) =>
{
    if (string.IsNullOrWhiteSpace(token)) // без токена форму не показываем
        return Results.Redirect($"/account/forgot?return={Uri.EscapeDataString(@return ?? "/")}");
    return Results.Content(ResetPasswordPage(token, @return ?? "/", error, minPasswordLength), "text/html; charset=utf-8");
});

app.MapPost("/account/reset", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens,
    IPasswordHasher<AppUser> hasher, IEmailSender email, IPwnedPasswordChecker pwned,
    IOpenIddictTokenManager tokenManager, IOpenIddictAuthorizationManager authManager, AuthAudit audit, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync();
    string token = form["token"]!;
    string ret = form["return"].ToString();
    string password = form["password"]!;
    string RetToReset(string err) => $"/account/reset?token={Uri.EscapeDataString(token)}&return={Uri.EscapeDataString(ret)}&error={err}";

    // Проверяем пароль ДО погашения токена: при ошибке форму можно повторить по той же ссылке.
    if (!PasswordPolicy.IsAcceptable(password, minPasswordLength, out _))
        return Results.Redirect(RetToReset("weak"));

    // Токен одноразовый: гасим и получаем пользователя. Недействителен/просрочен → просим новую ссылку.
    var userId = await tokens.ConsumeAsync(token, EmailTokenPurpose.ResetPassword, ct);
    var user = userId is { } id ? await db.Users.FindAsync([id], ct) : null;
    if (user is null)
        return Results.Redirect($"/account/forgot?return={Uri.EscapeDataString(ret)}&error=badtoken");

    if (checkPwned && await pwned.IsPwnedAsync(password, ct))
        return Results.Redirect(RetToReset("pwned"));

    user.PasswordHash = hasher.HashPassword(user, password);
    user.EmailConfirmed = true; // переход по ссылке из письма доказывает владение адресом
    user.SecurityStamp = Guid.NewGuid().ToString("N"); // инвалидирует ВСЕ cookie-сессии на всех устройствах
    await db.SaveChangesAsync(ct);

    // OWASP: смена пароля инвалидирует активные сессии — отзываем все OIDC-токены/разрешения пользователя,
    // чтобы украденные access/refresh-токены умерли. Security-stamp гасит и cookie-сессии IdP немедленно.
    var sub = user.Id.ToString();
    await foreach (var t in tokenManager.FindBySubjectAsync(sub, ct)) await tokenManager.TryRevokeAsync(t, ct);
    await foreach (var a in authManager.FindBySubjectAsync(sub, ct)) await authManager.TryRevokeAsync(a, ct);

    var (subject, html) = EmailTemplates.PasswordChanged(user.DisplayName, IsEnCulture());
    await email.SendAsync(user.Email, subject, html); // уведомление владельцу о смене пароля

    await SignInCookieAsync(ctx, user); // новый вход после смены пароля
    await audit.LogAsync(ctx, AuthEventType.PasswordReset, user.Email, user.Id);
    return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
}).RequireRateLimiting("auth"); // защита от перебора reset-токена

// ---------------- OpenIddict: authorization endpoint ----------------
app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext ctx, AuthDbContext db,
    IOpenIddictScopeManager scopeManager) =>
{
    var request = ctx.GetOpenIddictServerRequest()
        ?? throw new InvalidOperationException("Некорректный OpenID Connect запрос.");

    var returnUrl = ctx.Request.PathBase + ctx.Request.Path + ctx.Request.QueryString;
    var result = await ctx.AuthenticateAsync("idp");
    if (!result.Succeeded)
    {
        // Не залогинен → ведём на единую страницу входа, потом возвращаемся сюда.
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = returnUrl },
            ["idp"]);
    }

    var sub = result.Principal!.FindFirst("sub")?.Value;
    var user = Guid.TryParse(sub, out var userId) ? await db.Users.FindAsync(userId) : null;
    if (user is null)
    {
        // Cookie-сессия ссылается на несуществующего пользователя (напр. cookie от прежней БД).
        // Не падаем 500 — гасим протухшую cookie и отправляем на повторный вход.
        await ctx.SignOutAsync("idp");
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = returnUrl },
            ["idp"]);
    }

    var identity = new ClaimsIdentity(
        authenticationType: TokenValidationParameters.DefaultAuthenticationType,
        nameType: Claims.Name, roleType: Claims.Role);

    identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.EmailVerified, user.EmailConfirmed ? "true" : "false") // мягкий гейт: приложения гейтят по нему
            .SetClaim(Claims.Name, user.DisplayName);

    // Ролевая модель: админам выдаём claim role=admin (едет в токен — см. GetDestinations).
    if (AdminRoles.IsAdmin(adminEmails, user.Email))
        identity.SetClaim(Claims.Role, AdminRoles.Role);

    var principal = new ClaimsPrincipal(identity);
    principal.SetScopes(request.GetScopes());

    // aud токена выводится из ресурсов, привязанных к запрошенным scope (а не задаётся вручную).
    var resources = new List<string>();
    await foreach (var resource in scopeManager.ListResourcesAsync(principal.GetScopes()))
        resources.Add(resource);
    principal.SetResources(resources);

    foreach (var claim in principal.Claims)
        claim.SetDestinations(GetDestinations(claim));

    return Results.SignIn(principal, new AuthenticationProperties(), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});

// ---------------- OpenIddict: token endpoint (code + refresh) ----------------
app.MapPost("/connect/token", async (HttpContext ctx) =>
{
    var request = ctx.GetOpenIddictServerRequest()
        ?? throw new InvalidOperationException("Некорректный запрос токена.");

    if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
    {
        var principal = (await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal!;
        foreach (var claim in principal.Claims)
            claim.SetDestinations(GetDestinations(claim));
        return Results.SignIn(principal, new AuthenticationProperties(), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    return Results.Problem("Неподдерживаемый тип гранта.", statusCode: 400);
});

// ---------------- OpenIddict: userinfo ----------------
app.MapMethods("/connect/userinfo", ["GET", "POST"], async (HttpContext ctx, AuthDbContext db) =>
{
    var principal = (await ctx.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
    if (principal is null) return Results.Unauthorized();

    var sub = principal.GetClaim(Claims.Subject);
    var user = sub is not null ? await db.Users.FindAsync(Guid.Parse(sub)) : null;
    if (user is null) return Results.Unauthorized();

    var claims = new Dictionary<string, object>
    {
        [Claims.Subject] = user.Id.ToString(),
        [Claims.Email] = user.Email,
        [Claims.EmailVerified] = user.EmailConfirmed,
        [Claims.Name] = user.DisplayName
    };
    // Роль — и в userinfo (потребитель мапит её в principal через GetClaimsFromUserInfoEndpoint).
    if (AdminRoles.IsAdmin(adminEmails, user.Email)) claims[Claims.Role] = AdminRoles.Role;
    return Results.Json(claims);
});

// ---------------- Завершение SSO-сессии ----------------
app.MapMethods("/connect/logout", ["GET", "POST"], async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("idp");
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
});

// ---------------- Внутренний резолв email → sub (привязка ученика в ApiService) ----------------
app.MapPost("/internal/users/by-email", async (ByEmailRequest req, HttpRequest http, AuthDbContext db,
    CancellationToken ct) =>
{
    if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();

    var email = req.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
    return user is null
        ? Results.NotFound()
        : Results.Ok(new { sub = user.Id.ToString(), displayName = user.DisplayName });
});

// ---------------- Батч-резолв sub → профиль (человекочитаемый список подписок в админке) ----------------
// Возвращаем только найденных; неизвестные/невалидные sub просто отсутствуют в ответе (вызывающий мержит).
app.MapPost("/internal/users/by-subs", async (BySubsRequest req, HttpRequest http, AuthDbContext db,
    CancellationToken ct) =>
{
    if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();

    var ids = (req.Subs ?? [])
        .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
        .Where(g => g.HasValue).Select(g => g!.Value).Distinct().ToList();
    if (ids.Count == 0) return Results.Ok(Array.Empty<UserInfo>());

    var users = await db.Users.AsNoTracking()
        .Where(u => ids.Contains(u.Id))
        .Select(u => new UserInfo(u.Id.ToString(), u.Email, u.DisplayName))
        .ToListAsync(ct);
    return Results.Ok(users);
});

app.MapDefaultEndpoints();
app.Run();

static async Task SignInCookieAsync(HttpContext ctx, AppUser user)
{
    var identity = new ClaimsIdentity("idp");
    identity.AddClaim(new Claim("sub", user.Id.ToString()));
    identity.AddClaim(new Claim("name", user.DisplayName));
    identity.AddClaim(new Claim("email", user.Email));
    identity.AddClaim(new Claim("email_verified", user.EmailConfirmed ? "true" : "false"));
    identity.AddClaim(new Claim("sstamp", user.SecurityStamp)); // метка для мгновенной инвалидации сессий
    await ctx.SignInAsync("idp", new ClaimsPrincipal(identity));
}

// Выпускает токен подтверждения и шлёт письмо со ссылкой (абсолютный URL — по forwarded-хосту запроса).
static async Task SendConfirmationEmailAsync(HttpContext ctx, EmailTokenService tokens, IEmailSender email,
    AppUser user, string? ret)
{
    var raw = await tokens.CreateAsync(user.Id, EmailTokenPurpose.ConfirmEmail, EmailTokenService.ConfirmLifetime);
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    var link = $"{baseUrl}/account/confirm?token={Uri.EscapeDataString(raw)}&return={Uri.EscapeDataString(ret ?? "")}";
    var (subject, html) = EmailTemplates.ConfirmEmail(user.DisplayName, link, IsEnCulture());
    await email.SendAsync(user.Email, subject, html);
}

static bool IsEnCulture() => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en";

static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
{
    Claims.Name or Claims.Email or Claims.EmailVerified or Claims.Subject or Claims.Role => [Destinations.AccessToken, Destinations.IdentityToken],
    _ => [Destinations.AccessToken]
};

// Единый каркас страниц аккаунта (CSS/шапка один раз). bodyInner — готовая разметка карточки.
static string AuthShell(string lang, string title, string bodyInner) => $$"""
<!doctype html><html lang="{{lang}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{title}} — ChessSchool ID</title>
<style>
:root{--ink:#0e1116;--ink2:#5b6470;--muted:#8b93a1;--line:#d6dae1;--accent:#2b6ef2;--accent-h:#1f5ad8;--bg:#f6f7f9;--surface:#fff}
*{box-sizing:border-box}
body{font-family:-apple-system,"Segoe UI",Roboto,Arial,sans-serif;background:var(--bg);color:var(--ink);display:flex;justify-content:center;align-items:center;min-height:100dvh;margin:0;padding:16px}
.card{background:var(--surface);padding:2rem;border-radius:18px;width:340px;max-width:100%;border:1px solid var(--line);box-shadow:0 12px 40px rgba(14,17,22,.10)}
.brand{display:flex;align-items:center;gap:.55rem;font-weight:720;font-size:1.15rem;letter-spacing:-.02em;margin-bottom:.3rem}
.brand .logo{width:30px;height:30px;display:grid;place-items:center;background:var(--ink);border-radius:8px}
.sub{color:var(--muted);font-size:.85rem;margin:0 0 1.25rem}
h1{font-size:1.25rem;margin:0 0 1rem}
label{font-size:.8rem;color:var(--ink2);font-weight:600}
input{width:100%;padding:.6rem .7rem;margin:.25rem 0 .7rem;border-radius:8px;border:1px solid var(--line);background:var(--surface);color:var(--ink);font-size:.92rem}
input:focus{outline:0;border-color:var(--accent);box-shadow:0 0 0 3px #eaf1fe}
button{width:100%;padding:.65rem;border:0;border-radius:8px;background:var(--accent);color:#fff;font-weight:600;font-size:.95rem;cursor:pointer;margin-top:.3rem}
button:hover{background:var(--accent-h)}
.err{color:#e5484d;font-size:.85rem;background:#fdecec;padding:.5rem .7rem;border-radius:8px;margin:0 0 1rem}
.info{color:#0e6b52;font-size:.88rem;background:#e7f6ef;padding:.6rem .7rem;border-radius:8px;margin:0 0 1rem;line-height:1.5}
.switch{color:var(--ink2);font-size:.85rem;text-align:center;margin:1.1rem 0 0}
.switch a{color:var(--accent);font-weight:600;text-decoration:none}
.switch a:hover{text-decoration:underline}
.muted{color:var(--muted);font-size:.78rem;text-align:center;margin:1.1rem 0 0}
.resend{margin:0 0 1rem;padding:.6rem .7rem;background:#f6f7f9;border:1px solid var(--line);border-radius:8px}
.resend button{margin-top:.4rem}
#mode{display:none}
.view-reg{display:none}
#mode:checked ~ .card .view-login{display:none}
#mode:checked ~ .card .view-reg{display:block}
.switch .as-link{background:none;border:0;color:var(--accent);font-weight:600;cursor:pointer;font-size:.85rem;padding:0;width:auto;margin:0}
.switch .as-link:hover{background:none;text-decoration:underline}
</style></head>
<body>
{{bodyInner}}
</body></html>
""";

static string BrandHeader(string sub) =>
    $"""<div class="brand"><span class="logo"><svg viewBox="0 0 45 45" width="18" height="18" fill="#fff"><path d="M18 10c1-1 3-2 5-2 7 0 12 6 12 16v14H13c0-6 3-9 7-12-2 1-5 2-7 1-2-1-2-3-1-5-2 1-4 1-5-1-1-3 1-5 4-7 .5-1 1-2 0-3 1-1 2-1 3 0z"/></svg></span> ChessSchool ID</div><p class="sub">{sub}</p>""";

static string LoginPage(string ret, string? error, bool register, bool sent, string? email, int minPw)
{
    var en = IsEnCulture();
    string lang = en ? "en" : "ru";
    string retEnc = System.Net.WebUtility.HtmlEncode(ret);
    string retQ = Uri.EscapeDataString(ret);
    string emailEnc = System.Net.WebUtility.HtmlEncode(email ?? "");
    string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
    string secured = en ? "Secured by OpenID Connect" : "Защищено OpenID Connect";
    string resendBtn = en ? "Resend confirmation email" : "Отправить письмо ещё раз";

    // Состояние «письмо отправлено» — отдельная карточка (без переключателя вход/регистрация).
    if (sent)
    {
        string sTitle = en ? "Check your email" : "Проверьте почту";
        string sBody = en
            ? $"We sent a confirmation link to <b>{emailEnc}</b>. Open it to activate your account and finish signing in."
            : $"Мы отправили ссылку для подтверждения на <b>{emailEnc}</b>. Перейдите по ней, чтобы активировать аккаунт и войти.";
        string back = en ? "Back to sign in" : "Вернуться ко входу";
        string sentInner = $"""
<div class="card">
{BrandHeader(sub)}
<h1>{sTitle}</h1>
<p class="info">{sBody}</p>
<form method="post" action="/account/resend">
<input type="hidden" name="return" value="{retEnc}">
<input type="hidden" name="email" value="{emailEnc}">
<button type="submit">{resendBtn}</button></form>
<p class="switch"><a href="/account/login?return={retQ}">{back}</a></p>
<p class="muted">{secured}</p></div>
""";
        return AuthShell(lang, sTitle, sentInner);
    }

    string titleReg = en ? "Sign up" : "Регистрация", titleLogin = en ? "Sign in" : "Вход";
    string lPassword = en ? "Password" : "Пароль", lName = en ? "Name" : "Имя";
    string phName = en ? "Your name" : "Ваше имя", phPass6 = en ? $"At least {minPw} characters" : $"Минимум {minPw} символов";
    string btnLogin = en ? "Sign in" : "Войти", btnCreate = en ? "Create account" : "Создать аккаунт";
    string noAcc = en ? "No account?" : "Нет аккаунта?", doReg = en ? "Sign up" : "Зарегистрироваться";
    string haveAcc = en ? "Already have an account?" : "Уже есть аккаунт?";
    string forgot = en ? "Forgot password?" : "Забыли пароль?";

    string errText = error switch
    {
        "unconfirmed" => en ? "Please confirm your email first — we can resend the link." : "Сначала подтвердите e-mail — можем отправить ссылку ещё раз.",
        "badtoken" => en ? "The confirmation link is invalid or has expired. Request a new one:" : "Ссылка подтверждения недействительна или устарела. Запросите новую:",
        "exists" => en ? "This email is already registered. Sign in instead." : "Этот e-mail уже зарегистрирован. Войдите.",
        "weak" => en ? $"Password too short — at least {minPw} characters." : $"Пароль слишком короткий — минимум {minPw} символов.",
        "pwned" => en ? "This password appears in known data breaches. Choose another." : "Этот пароль есть в известных утечках — выберите другой.",
        null => "",
        _ => en ? "Invalid credentials or email already taken." : "Неверные данные или email уже занят.",
    };
    string errBlock = error is null ? "" : $"<p class=\"err\">{errText}</p>";
    // Форма повторной отправки письма: при unconfirmed — email известен (скрытое поле), при badtoken — вводится.
    string resendBlock = error switch
    {
        "unconfirmed" => $"""<form class="resend" method="post" action="/account/resend"><input type="hidden" name="return" value="{retEnc}"><input type="hidden" name="email" value="{emailEnc}"><button type="submit">{resendBtn}</button></form>""",
        "badtoken" => $"""<form class="resend" method="post" action="/account/resend"><input type="hidden" name="return" value="{retEnc}"><label>Email</label><input name="email" type="email" value="{emailEnc}" placeholder="you@example.com" required><button type="submit">{resendBtn}</button></form>""",
        _ => "",
    };

    string inner = $$"""
<input type="checkbox" id="mode" {{(register ? "checked" : "")}}>
<div class="card">
{{BrandHeader(sub)}}
{{errBlock}}
{{resendBlock}}
<div class="view-login">
<h1>{{titleLogin}}</h1>
<form method="post" action="/account/login">
<input type="hidden" name="return" value="{{retEnc}}">
<label>Email</label><input name="email" type="email" value="{{emailEnc}}" placeholder="you@example.com" required>
<label>{{lPassword}}</label><input name="password" type="password" placeholder="••••••••" required>
<button type="submit">{{btnLogin}}</button></form>
<p class="switch"><a href="/account/forgot?return={{retQ}}">{{forgot}}</a></p>
<p class="switch">{{noAcc}} <label for="mode" class="as-link">{{doReg}}</label></p>
</div>
<div class="view-reg">
<h1>{{titleReg}}</h1>
<form method="post" action="/account/register">
<input type="hidden" name="return" value="{{retEnc}}">
<label>{{lName}}</label><input name="name" placeholder="{{phName}}">
<label>Email</label><input name="email" type="email" value="{{emailEnc}}" placeholder="you@example.com" required>
<label>{{lPassword}}</label><input name="password" type="password" placeholder="{{phPass6}}" required>
<button type="submit">{{btnCreate}}</button></form>
<p class="switch">{{haveAcc}} <label for="mode" class="as-link">{{btnLogin}}</label></p>
</div>
<p class="muted">{{secured}}</p></div>
""";
    return AuthShell(lang, register ? titleReg : titleLogin, inner);
}

// Страница управления e-mail (вход есть): переотправка + смена адреса до подтверждения.
static string AccountEmailPage(string email, bool confirmed, string? pendingEmail, string ret, string? error)
{
    var en = IsEnCulture();
    string lang = en ? "en" : "ru";
    string retEnc = System.Net.WebUtility.HtmlEncode(ret);
    string emailEnc = System.Net.WebUtility.HtmlEncode(email);
    string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
    string title = en ? "Your email" : "Ваш e-mail";
    string back = en ? "← Back" : "← Назад";
    string resendBtn = en ? "Resend confirmation email" : "Отправить письмо ещё раз";
    string changeLbl = en ? "Wrong address? Change it" : "Не тот адрес? Изменить";
    string changeBtn = en ? "Change and resend" : "Изменить и переслать";

    string errText = error switch
    {
        "taken" => en ? "This email is already in use." : "Этот e-mail уже занят.",
        "invalid" => en ? "Enter a valid email." : "Укажите корректный e-mail.",
        _ => "",
    };
    string errBlockTop = string.IsNullOrEmpty(errText) ? "" : $"<p class=\"err\">{errText}</p>";

    if (confirmed)
    {
        // Подтверждённый адрес: смена по схеме verify-new-before-switch (ссылка на новый адрес; старый не меняется).
        string okMsg = en ? "Your e-mail is confirmed ✓" : "Ваш e-mail подтверждён ✓";
        string changeConfirmedLbl = en ? "Change email" : "Изменить e-mail";
        string changeConfirmedBtn = en ? "Send confirmation to new address" : "Отправить подтверждение на новый адрес";
        string pendingBlock = string.IsNullOrEmpty(pendingEmail) ? "" : (en
            ? $"<p class=\"info\">Pending confirmation at <b>{System.Net.WebUtility.HtmlEncode(pendingEmail)}</b>. The change applies once confirmed.</p>"
            : $"<p class=\"info\">Ожидает подтверждения на <b>{System.Net.WebUtility.HtmlEncode(pendingEmail)}</b>. Смена вступит в силу после подтверждения.</p>");
        return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlockTop}<p class="info">{okMsg} <b>{emailEnc}</b></p>{pendingBlock}
<div class="resend"><label>{changeConfirmedLbl}</label>
<form method="post" action="/account/change-email"><input type="hidden" name="return" value="{retEnc}"><input name="email" type="email" placeholder="new@example.com" required><button type="submit">{changeConfirmedBtn}</button></form></div>
<p class="switch"><a href="{retEnc}">{back}</a></p></div>
""");
    }

    string pending = en
        ? $"We sent a confirmation link to <b>{emailEnc}</b>. Not confirmed yet — resend it or fix the address."
        : $"Мы отправили ссылку на <b>{emailEnc}</b>. Пока не подтверждён — переотправьте или исправьте адрес.";
    return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlockTop}
<p class="info">{pending}</p>
<form method="post" action="/account/resend"><input type="hidden" name="return" value="{retEnc}"><input type="hidden" name="email" value="{emailEnc}"><button type="submit">{resendBtn}</button></form>
<div class="resend"><label>{changeLbl}</label>
<form method="post" action="/account/change-email"><input type="hidden" name="return" value="{retEnc}"><input name="email" type="email" value="{emailEnc}" placeholder="you@example.com" required><button type="submit">{changeBtn}</button></form></div>
<p class="switch"><a href="{retEnc}">{back}</a></p></div>
""");
}

// Страница запроса сброса пароля: ввод e-mail + нейтральное состояние «письмо отправлено».
static string ForgotPasswordPage(string ret, bool sent, string? email, string? error)
{
    var en = IsEnCulture();
    string lang = en ? "en" : "ru";
    string retEnc = System.Net.WebUtility.HtmlEncode(ret);
    string retQ = Uri.EscapeDataString(ret);
    string emailEnc = System.Net.WebUtility.HtmlEncode(email ?? "");
    string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
    string secured = en ? "Secured by OpenID Connect" : "Защищено OpenID Connect";
    string title = en ? "Reset password" : "Сброс пароля";
    string back = en ? "Back to sign in" : "Вернуться ко входу";

    if (sent)
    {
        string sBody = en
            ? "If an account exists for that email, we've sent a link to reset the password. The link is valid for 1 hour."
            : "Если аккаунт с таким e-mail существует, мы отправили ссылку для сброса пароля. Ссылка действительна 1 час.";
        return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1><p class="info">{sBody}</p>
<p class="switch"><a href="/account/login?return={retQ}">{back}</a></p>
<p class="muted">{secured}</p></div>
""");
    }

    string lead = en
        ? "Enter your email and we'll send a link to reset your password."
        : "Введите e-mail — пришлём ссылку для сброса пароля.";
    string btn = en ? "Send reset link" : "Отправить ссылку";
    string errText = error == "badtoken"
        ? (en ? "The reset link is invalid or has expired. Request a new one:" : "Ссылка сброса недействительна или устарела. Запросите новую:")
        : "";
    string errBlock = string.IsNullOrEmpty(errText) ? "" : $"<p class=\"err\">{errText}</p>";
    return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlock}
<p class="sub">{lead}</p>
<form method="post" action="/account/forgot">
<input type="hidden" name="return" value="{retEnc}">
<label>Email</label><input name="email" type="email" value="{emailEnc}" placeholder="you@example.com" required>
<button type="submit">{btn}</button></form>
<p class="switch"><a href="/account/login?return={retQ}">{back}</a></p>
<p class="muted">{secured}</p></div>
""");
}

// Страница ввода нового пароля по ссылке из письма (token в скрытом поле).
static string ResetPasswordPage(string token, string ret, string? error, int minPw)
{
    var en = IsEnCulture();
    string lang = en ? "en" : "ru";
    string retEnc = System.Net.WebUtility.HtmlEncode(ret);
    string tokenEnc = System.Net.WebUtility.HtmlEncode(token);
    string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
    string secured = en ? "Secured by OpenID Connect" : "Защищено OpenID Connect";
    string title = en ? "New password" : "Новый пароль";
    string lPassword = en ? "New password" : "Новый пароль";
    string ph = en ? $"At least {minPw} characters" : $"Минимум {minPw} символов";
    string btn = en ? "Save new password" : "Сохранить пароль";

    string errText = error switch
    {
        "weak" => en ? $"Password too short — at least {minPw} characters." : $"Пароль слишком короткий — минимум {minPw} символов.",
        "pwned" => en ? "This password appears in known data breaches. Choose another." : "Этот пароль есть в известных утечках — выберите другой.",
        _ => "",
    };
    string errBlock = string.IsNullOrEmpty(errText) ? "" : $"<p class=\"err\">{errText}</p>";
    return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlock}
<form method="post" action="/account/reset">
<input type="hidden" name="token" value="{tokenEnc}">
<input type="hidden" name="return" value="{retEnc}">
<label>{lPassword}</label><input name="password" type="password" placeholder="{ph}" required>
<button type="submit">{btn}</button></form>
<p class="muted">{secured}</p></div>
""");
}

record ByEmailRequest(string Email);
