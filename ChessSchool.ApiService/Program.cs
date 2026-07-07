using System.Net.Http.Json;
using ChessSchool.ApiService;
using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Domain;
using ChessSchool.ApiService.Services;
using ChessSchool.ApiService.Services.Billing;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// БД — PostgreSQL, по одной на bounded-контекст (строки подключения инжектит Aspire):
// schooldb (школьный домен), arenadb (архив арена-партий), billingdb (подписки/биллинг).
builder.Services.AddDbContext<SchoolDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("schooldb")));
builder.Services.AddDbContext<ArenaDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("arenadb")));
builder.Services.AddDbContext<BillingDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("billingdb")));
builder.Services.AddSingleton<IRatingService, Glicko2RatingService>();
builder.Services.AddScoped<GameArchiver>();
builder.Services.AddScoped<ArenaGameStore>();
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddScoped<StudentService>();
builder.Services.AddScoped<SchoolAccessService>(); // авторизация по владению школой + провижининг «моя школа»
// В Development новосозданная школа наполняется примерными учениками (ЛК не пуст при первом входе); в проде — пустая.
builder.Services.AddSingleton(new SchoolProvisioningOptions(builder.Environment.IsDevelopment()));
// Провайдер эквайринга: Paddle при наличии конфига (секрет вебхука/API-ключ), иначе dev-заглушка
// (оплата проходит локально). Выбор по конфигу — как S3↔MinIO.
var paddleOptions = builder.Configuration.GetSection("Paddle").Get<PaddleOptions>() ?? new PaddleOptions();
if (!string.IsNullOrWhiteSpace(paddleOptions.WebhookSecret) || !string.IsNullOrWhiteSpace(paddleOptions.ApiKey))
{
    builder.Services.AddSingleton(paddleOptions);
    builder.Services.AddHttpClient(PaddleBillingProvider.HttpClientName, c => c.BaseAddress =
        new(paddleOptions.Environment == "production" ? "https://api.paddle.com" : "https://sandbox-api.paddle.com"));
    builder.Services.AddSingleton<IBillingProvider, PaddleBillingProvider>();
}
else
{
    builder.Services.AddSingleton<IBillingProvider, DevStubBillingProvider>();
}
builder.AddChessSchoolAnalytics();

// Readiness-проверка доступности БД (попадает в /health, не в /alive). Без строки подключения
// (InMemory в тестах) — пропускаем. По одной проверке на каждую БД.
foreach (var (name, cs) in new[] { ("schooldb", "schooldb"), ("arenadb", "arenadb"), ("billingdb", "billingdb") })
    if (builder.Configuration.GetConnectionString(cs) is { Length: > 0 } conn)
        builder.Services.AddHealthChecks().AddNpgSql(conn, name: name);

// Клиент к сервису авторизации (для резолва email → sub при привязке аккаунта).
builder.Services.AddHttpClient("auth", c => c.BaseAddress = new("https+http://auth"));

// Ключ для server-to-server вызовов от GameServer (архивация онлайн-партий).
// Вне Development обязателен реальный секрет — иначе старт падает (см. ResolveInternalApiKey).
var internalKey = builder.Configuration.ResolveInternalApiKey(builder.Environment);

// Клиент к IdP для резолва пользователей (привязка ученика по e-mail, обогащение списка подписок именами).
builder.Services.AddSingleton(sp => new IdpUserClient(
    sp.GetRequiredService<IHttpClientFactory>(), internalKey, sp.GetRequiredService<ILogger<IdpUserClient>>()));

var app = builder.Build();

// Применение схемы. В проде миграции — ОТДЕЛЬНЫМ шагом (тот же образ с аргументом `migrate` как k8s Job),
// боевые реплики стартуют без авто-миграции (нет гонки реплик). Флаг Database:MigrateAtStartup
// (по умолчанию = Development) и режим `migrate` это включают.
var migrateRequested = args.Contains("migrate");
var migrateAtStartup = builder.Configuration.GetValue("Database:MigrateAtStartup", builder.Environment.IsDevelopment());
using (var scope = app.Services.CreateScope())
{
    // Каждый bounded-контекст — своя БД со своим __EFMigrationsHistory. Применяем все три.
    void ApplySchema(DbContext ctx)
    {
        if (!ctx.Database.IsNpgsql()) ctx.Database.EnsureCreated();     // InMemory (тесты)
        else if (migrateRequested || migrateAtStartup) ctx.Database.Migrate();
    }

    var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
    ApplySchema(db);
    ApplySchema(scope.ServiceProvider.GetRequiredService<ArenaDbContext>());
    ApplySchema(scope.ServiceProvider.GetRequiredService<BillingDbContext>());
    // Демо-данные (школа/ученики) — только вне прод-БД и не в чистом режиме миграции.
    if (!migrateRequested && app.Environment.IsDevelopment()) SeedData.Ensure(db);
}
if (migrateRequested) return; // режим миграции: схему применили — выходим (job завершён)

app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

// ---------- ЛК школы: доступ ТОЛЬКО владельцу (BFF) ----------
// Web ходит server-to-server с X-Internal-Key и передаёт вошедшего пользователя в X-Acting-Sub;
// ApiService проверяет владение школой (School.OwnerSub == acting sub). Форбид = 403 через StatusCode
// (в ApiService нет auth-схемы, поэтому Results.Forbid() неприменим). 403-константа:
const int Forbidden = StatusCodes.Status403Forbidden;
var lk = app.MapGroup("").RequireInternalKey(internalKey).RequireActingSub();

// Провижининг: школа текущего пользователя (или создаётся) — Web зовёт вместо фикс. Demo.SchoolId.
lk.MapGet("/my-school", async (HttpContext ctx, SchoolAccessService access, CancellationToken ct) =>
    Results.Ok(await access.EnsureSchoolForAsync(ctx.ActingSub()!, ct)));

lk.MapGet("/schools/{schoolId:guid}/students",
    async (Guid schoolId, int? skip, int? take, HttpContext ctx, StudentService students, SchoolAccessService access, CancellationToken ct) =>
    !await access.OwnsSchoolAsync(ctx.ActingSub()!, schoolId, ct) ? Results.StatusCode(Forbidden)
    : Results.Ok(await students.ListBySchoolAsync(schoolId, skip, take, ct)));

lk.MapGet("/students/{id:guid}", async (Guid id, HttpContext ctx, StudentService students, SchoolAccessService access, CancellationToken ct) =>
    !await access.OwnsStudentAsync(ctx.ActingSub()!, id, ct) ? Results.StatusCode(Forbidden)
    : await students.GetProfileAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());

lk.MapGet("/schools/{schoolId:guid}/pending-games",
    async (Guid schoolId, int? skip, int? take, HttpContext ctx, StudentService students, SchoolAccessService access, CancellationToken ct) =>
    !await access.OwnsSchoolAsync(ctx.ActingSub()!, schoolId, ct) ? Results.StatusCode(Forbidden)
    : Results.Ok(await students.ListPendingGamesAsync(schoolId, skip, take, ct)));

lk.MapPost("/schools/{schoolId:guid}/students", async (Guid schoolId, CreateStudentRequest req,
    HttpContext ctx, StudentService students, SchoolAccessService access, CancellationToken ct) =>
{
    if (!await access.OwnsSchoolAsync(ctx.ActingSub()!, schoolId, ct)) return Results.StatusCode(Forbidden);
    var (dto, error) = await students.CreateAsync(schoolId, req, ct);
    return error is not null ? Results.BadRequest(new { error }) : Results.Created($"/students/{dto!.Id}", dto);
});

lk.MapPut("/students/{id:guid}", async (Guid id, UpdateStudentRequest req,
    HttpContext ctx, StudentService students, SchoolAccessService access, CancellationToken ct) =>
{
    if (!await access.OwnsStudentAsync(ctx.ActingSub()!, id, ct)) return Results.StatusCode(Forbidden);
    if (string.IsNullOrWhiteSpace(req.DisplayName)) return Results.BadRequest(new { error = "Имя не может быть пустым." });
    var dto = await students.UpdateAsync(id, req with { DisplayName = req.DisplayName.Trim() }, ct);
    return dto is not null ? Results.Ok(dto) : Results.NotFound();
});

lk.MapPost("/games/{id:guid}/attribute", async (Guid id, AttributeGameRequest req,
    HttpContext ctx, StudentService students, SchoolAccessService access, CancellationToken ct) =>
{
    // Оба ученика должны быть в школе владельца — заодно закрывает cross-school атрибуцию.
    var sub = ctx.ActingSub()!;
    if (!await access.OwnsStudentAsync(sub, req.WhiteStudentId, ct) || !await access.OwnsStudentAsync(sub, req.BlackStudentId, ct))
        return Results.StatusCode(Forbidden);
    return await students.AttributeAsync(id, req, ct) switch
    {
        StudentService.AttributeOutcome.GameNotFound => Results.NotFound(),
        StudentService.AttributeOutcome.StudentNotFound => Results.BadRequest(new { error = "Ученик не найден." }),
        _ => Results.Ok(),
    };
});

lk.MapPost("/students/{id:guid}/link", async (Guid id, LinkAccountRequest req,
    HttpContext ctx, StudentService students, SchoolAccessService access, CancellationToken ct) =>
{
    if (!await access.OwnsStudentAsync(ctx.ActingSub()!, id, ct)) return Results.StatusCode(Forbidden);
    var (outcome, dto) = await students.LinkAsync(id, req.Email, ct);
    return outcome switch
    {
        StudentService.LinkOutcome.StudentNotFound => Results.NotFound(),
        StudentService.LinkOutcome.UserNotFound => Results.BadRequest(new { error = "Пользователь с таким email не найден в IdP." }),
        _ => Results.Ok(dto),
    };
});

lk.MapPost("/students/{id:guid}/share", async (Guid id, HttpContext ctx, StudentService students, SchoolAccessService access, CancellationToken ct) =>
    !await access.OwnsStudentAsync(ctx.ActingSub()!, id, ct) ? Results.StatusCode(Forbidden)
    : await students.CreateShareAsync(id, ct) is { } link ? Results.Ok(link) : Results.NotFound());

// Публичный: профиль по share-токену — capability-URL родителю, БЕЗ ключа и acting-sub (вне группы `lk`).
app.MapGet("/share/{token}", async (string token, StudentService students, CancellationToken ct) =>
    await students.GetSharedProfileAsync(token, ct) is { } p ? Results.Ok(p) : Results.NotFound());

// ---------- Внутренние эндпоинты (server-to-server) — все под одним гейтом X-Internal-Key ----------
// Гейт навешен на группу один раз (см. RequireInternalKey), а не дублируется в каждом обработчике.
var internalApi = app.MapGroup("/internal").RequireInternalKey(internalKey);

// Приём онлайн-партий от GameServer.
internalApi.MapPost("/games/archive", async (ArchiveGameRequest req, GameArchiver archiver, CancellationToken ct) =>
    Results.Ok(new { archived = await archiver.ArchiveOnlineAsync(req, ct) }));

// --- Арена-партии (B2C): архив + история игрока + разбор. ---
internalApi.MapPost("/arena-games/archive", async (ArenaGameArchiveRequest req, ArenaGameStore store, CancellationToken ct) =>
    Results.Ok(new { archived = await store.ArchiveAsync(req, ct) }));

internalApi.MapGet("/arena-games", async (string sub, int? skip, int? take, ArenaGameStore store, CancellationToken ct) =>
{
    var t = Math.Clamp(take ?? 20, 1, 100); // лимит выборки — без «отдай всё»
    return Results.Ok(await store.ListForPlayerAsync(sub, Math.Max(0, skip ?? 0), t, ct));
});

internalApi.MapGet("/arena-games/stats", async (string sub, ArenaGameStore store, CancellationToken ct) =>
    Results.Ok(await store.GetStatsAsync(sub, ct)));

internalApi.MapGet("/arena-games/{id:guid}", async (Guid id, string sub, ArenaGameStore store, CancellationToken ct) =>
    await store.GetForPlayerAsync(id, sub, ct) is { } g ? Results.Ok(g) : Results.NotFound());

internalApi.MapGet("/arena-games/{id:guid}/analysis", async (Guid id, string sub, ArenaGameStore store, CancellationToken ct) =>
    await store.GetAnalysisJsonAsync(id, sub, ct) is { } json
        ? Results.Content(json, "application/json") : Results.NoContent());

internalApi.MapPost("/arena-games/{id:guid}/analysis", async (Guid id, HttpRequest http,
    ArenaGameStore store, CancellationToken ct) =>
{
    using var reader = new StreamReader(http.Body);
    var json = await reader.ReadToEndAsync(ct);
    await store.SaveAnalysisJsonAsync(id, json, ct);
    return Results.Ok();
});

// Entitlement подписки для потребителей (Arena/Web).
internalApi.MapGet("/subscriptions/{sub}", async (string sub, SubscriptionService subs, CancellationToken ct) =>
    Results.Ok(await subs.GetAsync(sub, ct)));

// Customer Portal: сессия hosted-портала провайдера (отмена/смена карты) для пользователя.
internalApi.MapGet("/subscriptions/{sub}/portal", async (string sub,
    SubscriptionService subsSvc, IBillingProvider billing, CancellationToken ct) =>
{
    var customerId = await subsSvc.GetProviderCustomerIdAsync(sub, ct);
    var url = string.IsNullOrEmpty(customerId) ? null : await billing.CreatePortalUrlAsync(customerId, ct);
    return Results.Ok(new PortalLinkDto(url));
});

// Вытягивание статуса (reconcile из API провайдера, если вебхук не дошёл). Сначала по сохранённой
// подписке; если её нет/не дала результата — по e-mail пользователя (надёжное восстановление: работает,
// даже когда строки подписки у нас нет — например, после ручного снятия в админке).
internalApi.MapPost("/subscriptions/{sub}/refresh", async (string sub, string? email,
    SubscriptionService subsSvc, IBillingProvider billing, CancellationToken ct) =>
{
    BillingEventDto? state = null;
    var subId = await subsSvc.GetProviderSubscriptionIdAsync(sub, ct);
    if (!string.IsNullOrEmpty(subId)) state = await billing.FetchSubscriptionAsync(subId, ct, sub);
    if (state is null && !string.IsNullOrWhiteSpace(email))
        state = await billing.FetchByCustomerEmailAsync(email, sub, ct);
    if (state is not null) await subsSvc.ReconcileAsync(state, ct);
    return Results.Ok(await subsSvc.GetAsync(sub, ct));
});

// Reconcile по transaction id из success-URL checkout — активирует премиум без вебхука (user_sub из txn).
internalApi.MapPost("/subscriptions/reconcile-transaction", async (ReconcileTxnRequest req,
    SubscriptionService subsSvc, IBillingProvider billing, CancellationToken ct) =>
{
    var state = await billing.FetchByTransactionAsync(req.TransactionId, ct);
    if (state is not null) await subsSvc.ReconcileAsync(state, ct);
    return Results.Ok(state is null ? null : await subsSvc.GetAsync(state.UserSub, ct));
});

// ---------------- Админ-управление подписками (ручная выдача/снятие, сдвиг срока для теста) --------
// Защита — внутренний ключ (вызывает только Arena из-под политики Admin). Это сознательный обход
// провайдера: выданное вручную состояние — источник истины наравне с вебхуком (last-write-wins;
// вебхук Paddle может его позже переписать). Резолв e-mail/имени — батчем в IdP (деградирует тихо).
internalApi.MapGet("/admin/subscriptions", async (int? take,
    SubscriptionService subs, IdpUserClient idp, CancellationToken ct) =>
{
    var rows = await subs.ListAsync(take ?? 500, ct);
    var users = await idp.ResolveBySubsAsync(rows.Select(r => r.UserSub).Distinct().ToList(), ct);
    var enriched = rows.Select(r => users.TryGetValue(r.UserSub, out var u)
        ? r with { Email = u.Email, DisplayName = u.DisplayName } : r).ToList();
    return Results.Ok(enriched);
});

internalApi.MapPost("/admin/subscriptions/{sub}", async (string sub, AdminSetSubscriptionRequest req,
    SubscriptionService subs, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(sub)) return Results.BadRequest(new { error = "Пустой sub." });
    return Results.Ok(await subs.AdminSetAsync(sub, req.Status, req.Plan, req.CurrentPeriodEnd, ct));
});

internalApi.MapPost("/admin/subscriptions/by-email", async (AdminSetByEmailRequest req,
    SubscriptionService subs, IdpUserClient idp, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "Не указан e-mail." });

    var found = await idp.ResolveByEmailAsync(req.Email, ct);
    if (found is null) return Results.NotFound(new { error = "Пользователь с таким e-mail не найден в IdP." });

    var dto = await subs.AdminSetAsync(found.Sub, req.Status, req.Plan, req.CurrentPeriodEnd, ct);
    return Results.Ok(new { sub = found.Sub, subscription = dto });
});

internalApi.MapDelete("/admin/subscriptions/{sub}", async (string sub, SubscriptionService subs, CancellationToken ct) =>
    await subs.AdminRemoveAsync(sub, ct) ? Results.Ok() : Results.NotFound());

// Dev-активация премиума без провайдера (только Development) — локальный тест гейтинга.
if (app.Environment.IsDevelopment())
{
    internalApi.MapPost("/subscriptions/dev-activate", async (DevActivateRequest req,
        SubscriptionService subs, CancellationToken ct) =>
    {
        await subs.ApplyAsync(new BillingEventDto($"dev-{Guid.NewGuid():N}", req.UserSub,
            SubscriptionStatus.Active, req.Plan ?? "premium",
            CurrentPeriodEnd: DateTimeOffset.UtcNow.AddMonths(1)), ct);
        return Results.Ok(await subs.GetAsync(req.UserSub, ct));
    });
}

// Вебхук Paddle: публичный (Paddle вызывает извне), защита — подпись Paddle-Signature, не ключ.
// Идемпотентность — в SubscriptionService. На дубль/нерелевантное отвечаем 200, чтобы Paddle не ретраил.
app.MapPost("/webhooks/paddle", async (HttpRequest http, SubscriptionService subs,
    IConfiguration cfg, CancellationToken ct) =>
{
    var secret = cfg["Paddle:WebhookSecret"];
    if (string.IsNullOrEmpty(secret)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    using var reader = new StreamReader(http.Body);
    var body = await reader.ReadToEndAsync(ct);
    if (!PaddleWebhook.VerifySignature(body, http.Headers["Paddle-Signature"], secret, DateTimeOffset.UtcNow))
        return Results.BadRequest("invalid signature");

    if (PaddleWebhook.TryParse(body, out var ev) && ev is not null)
        await subs.ApplyAsync(ev, ct);
    return Results.Ok();
});

app.MapGet("/", () => "ChessSchool API. ЛК: /schools/{id}/students");
app.MapDefaultEndpoints();
app.Run();

namespace ChessSchool.ApiService
{
    /// <summary>Маркерный тип для WebApplicationFactory в тестах (уникальное имя, без конфликта с Program).</summary>
    public sealed class ApiServiceMarker;
}
