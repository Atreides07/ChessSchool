using System.Net.Http.Json;
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

// БД — PostgreSQL (connection string инжектит Aspire по ссылке на ресурс "school").
builder.Services.AddDbContext<SchoolDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("schooldb")));
builder.Services.AddSingleton<IRatingService, Glicko2RatingService>();
builder.Services.AddScoped<GameArchiver>();
builder.Services.AddScoped<SubscriptionService>();
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
// (InMemory в тестах) — пропускаем.
if (builder.Configuration.GetConnectionString("schooldb") is { Length: > 0 } schoolConn)
    builder.Services.AddHealthChecks().AddNpgSql(schoolConn, name: "postgres");

// Клиент к сервису авторизации (для резолва email → sub при привязке аккаунта).
builder.Services.AddHttpClient("auth", c => c.BaseAddress = new("https+http://auth"));

// Ключ для server-to-server вызовов от GameServer (архивация онлайн-партий).
// Вне Development обязателен реальный секрет — иначе старт падает (см. ResolveInternalApiKey).
var internalKey = builder.Configuration.ResolveInternalApiKey(builder.Environment);

var app = builder.Build();

// Применение схемы. В проде миграции — ОТДЕЛЬНЫМ шагом (тот же образ с аргументом `migrate` как k8s Job),
// боевые реплики стартуют без авто-миграции (нет гонки реплик). Флаг Database:MigrateAtStartup
// (по умолчанию = Development) и режим `migrate` это включают.
var migrateRequested = args.Contains("migrate");
var migrateAtStartup = builder.Configuration.GetValue("Database:MigrateAtStartup", builder.Environment.IsDevelopment());
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SchoolDbContext>();
    if (!db.Database.IsNpgsql()) db.Database.EnsureCreated();          // InMemory (тесты)
    else if (migrateRequested || migrateAtStartup) db.Database.Migrate();
    // Демо-данные (школа/ученики) — только вне прод-БД и не в чистом режиме миграции.
    if (!migrateRequested && app.Environment.IsDevelopment()) SeedData.Ensure(db);
}
if (migrateRequested) return; // режим миграции: схему применили — выходим (job завершён)

app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) app.MapOpenApi();

// ---------- Маппинг доменных сущностей в DTO ----------
static StudentDto ToDto(Student s) =>
    new(s.Id, s.GroupId, s.DisplayName, s.Rating, s.RatingDeviation, s.GamesPlayed, s.Wins, s.Draws, s.Losses, s.LinkedUserSub);

async Task<StudentProfileDto?> BuildProfileAsync(SchoolDbContext db, Guid studentId, CancellationToken ct)
{
    var student = await db.Students.FindAsync([studentId], ct);
    if (student is null) return null;

    var history = await db.RatingPoints.AsNoTracking()
        .Where(r => r.StudentId == studentId)
        .OrderBy(r => r.Date)
        .Select(r => new RatingPointDto(r.Date, r.Rating))
        .ToListAsync(ct);

    // Берём только нужные колонки последних 10 партий (без трекинга и лишних полей).
    var games = await db.Games.AsNoTracking()
        .Where(g => g.WhiteStudentId == studentId || g.BlackStudentId == studentId)
        .OrderByDescending(g => g.PlayedAt).Take(10)
        .Select(g => new
        {
            g.Id,
            g.PlayedAt,
            g.WhiteStudentId,
            g.BlackStudentId,
            g.Result,
            g.WhiteRatingChange,
            g.BlackRatingChange,
            g.Pgn
        })
        .ToListAsync(ct);

    // Имена соперников — только по фактически встретившимся id (а не вся таблица учеников).
    var oppIds = games
        .Select(g => g.WhiteStudentId == studentId ? g.BlackStudentId : g.WhiteStudentId)
        .OfType<Guid>().Distinct().ToList();
    var names = await db.Students.AsNoTracking()
        .Where(s => oppIds.Contains(s.Id))
        .Select(s => new { s.Id, s.DisplayName })
        .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);

    var summaries = games.Select(g =>
    {
        bool isWhite = g.WhiteStudentId == studentId;
        var oppId = isWhite ? g.BlackStudentId : g.WhiteStudentId;
        var oppName = oppId is { } id && names.TryGetValue(id, out var n) ? n : "Гость";
        return new GameSummaryDto(g.Id, g.PlayedAt, oppName,
            isWhite ? PieceColor.White : PieceColor.Black, g.Result,
            isWhite ? g.WhiteRatingChange : g.BlackRatingChange, g.Pgn);
    }).ToList();

    return new StudentProfileDto(ToDto(student), history, summaries);
}

// Пагинация: единый разбор и ограничение страницы (защита от выборки «всё» на больших таблицах).
static (int Skip, int Take) Page(int? skip, int? take, int maxTake = 200, int defaultTake = 100) =>
    (Math.Max(0, skip ?? 0), Math.Clamp(take ?? defaultTake, 1, maxTake));

// ---------- ЛК школы (чтение) ----------
app.MapGet("/schools/{schoolId:guid}/students",
    async (Guid schoolId, int? skip, int? take, SchoolDbContext db, CancellationToken ct) =>
{
    var (s, t) = Page(skip, take);
    // Один запрос с join, проекцией в DTO и пагинацией — без трекинга и без выборки всей таблицы.
    var students = await (
        from st in db.Students.AsNoTracking()
        join g in db.Groups on st.GroupId equals g.Id
        where g.SchoolId == schoolId
        orderby st.Rating descending
        select new StudentDto(st.Id, st.GroupId, st.DisplayName, st.Rating, st.RatingDeviation,
            st.GamesPlayed, st.Wins, st.Draws, st.Losses, st.LinkedUserSub))
        .Skip(s).Take(t).ToListAsync(ct);
    return Results.Ok(students);
});

app.MapGet("/students/{id:guid}", async (Guid id, SchoolDbContext db, CancellationToken ct) =>
    await BuildProfileAsync(db, id, ct) is { } p ? Results.Ok(p) : Results.NotFound());

app.MapGet("/schools/{schoolId:guid}/pending-games",
    async (Guid schoolId, int? skip, int? take, SchoolDbContext db, CancellationToken ct) =>
{
    var (s, t) = Page(skip, take);
    var pending = await db.Games.AsNoTracking()
        .Where(g => g.Source == AttributionSource.None && g.WhiteStudentId == null)
        .OrderByDescending(g => g.PlayedAt)
        .Skip(s).Take(t)
        .Select(g => new PendingGameDto(g.Id, g.PlayedAt, g.DeviceRef ?? "—", g.Pgn))
        .ToListAsync(ct);
    return Results.Ok(pending);
});

// ---------- ЛК школы (мутации) ----------
// Для локального демо открыты; в проде гейтятся JWT от IdP (см. docs).
app.MapPost("/schools/{schoolId:guid}/students", async (Guid schoolId, CreateStudentRequest req,
    SchoolDbContext db, IAnalytics analytics, CancellationToken ct) =>
{
    if (!await db.Groups.AnyAsync(g => g.Id == req.GroupId && g.SchoolId == schoolId, ct))
        return Results.BadRequest(new { error = "Группа не найдена в этой школе." });

    var student = new Student { GroupId = req.GroupId, DisplayName = req.DisplayName, BirthDate = req.BirthDate };
    db.Students.Add(student);
    await db.SaveChangesAsync(ct);
    analytics.Capture("student_created", schoolId.ToString(), new Dictionary<string, object?> { ["group_id"] = req.GroupId });
    return Results.Created($"/students/{student.Id}", ToDto(student));
});

app.MapPost("/games/{id:guid}/attribute", async (Guid id, AttributeGameRequest req,
    SchoolDbContext db, GameArchiver archiver, IAnalytics analytics, CancellationToken ct) =>
{
    var game = await db.Games.FindAsync([id], ct);
    if (game is null) return Results.NotFound();
    var white = await db.Students.FindAsync([req.WhiteStudentId], ct);
    var black = await db.Students.FindAsync([req.BlackStudentId], ct);
    if (white is null || black is null) return Results.BadRequest(new { error = "Ученик не найден." });

    await archiver.AttributeAsync(game, white, black, req.Result, ct);
    analytics.Capture("game_attributed", id.ToString(), new Dictionary<string, object?> { ["result"] = req.Result.ToString() });
    return Results.Ok();
});

// ---------- Привязка ученика к онлайн-аккаунту (по email из IdP) ----------
app.MapPost("/students/{id:guid}/link", async (Guid id, LinkAccountRequest req, SchoolDbContext db,
    IHttpClientFactory httpFactory, IAnalytics analytics, CancellationToken ct) =>
{
    var student = await db.Students.FindAsync([id], ct);
    if (student is null) return Results.NotFound();

    var client = httpFactory.CreateClient("auth");
    var msg = new HttpRequestMessage(HttpMethod.Post, "/internal/users/by-email")
    {
        Content = JsonContent.Create(new { email = req.Email })
    };
    msg.Headers.Add("X-Internal-Key", internalKey);
    var resp = await client.SendAsync(msg, ct);
    if (!resp.IsSuccessStatusCode)
        return Results.BadRequest(new { error = "Пользователь с таким email не найден в IdP." });

    var found = await resp.Content.ReadFromJsonAsync<ResolvedUser>(ct);
    student.LinkedUserSub = found!.Sub;
    await db.SaveChangesAsync(ct);
    analytics.Capture("student_account_linked", found.Sub, new Dictionary<string, object?> { ["student_id"] = id });
    return Results.Ok(ToDto(student));
});

// ---------- Шаринг профиля родителю ----------
app.MapPost("/students/{id:guid}/share", async (Guid id, SchoolDbContext db, IAnalytics analytics, CancellationToken ct) =>
{
    if (!await db.Students.AnyAsync(s => s.Id == id, ct)) return Results.NotFound();
    var token = Guid.NewGuid().ToString("N");
    db.ShareLinks.Add(new ShareLink { StudentId = id, Token = token, ExpiresAt = DateTimeOffset.UtcNow.AddDays(90) });
    await db.SaveChangesAsync(ct);
    analytics.Capture("share_link_created", id.ToString());
    return Results.Ok(new ShareLinkDto(token, $"/p/{token}", DateTimeOffset.UtcNow.AddDays(90)));
});

app.MapGet("/share/{token}", async (string token, SchoolDbContext db, IAnalytics analytics, CancellationToken ct) =>
{
    var link = await db.ShareLinks.FirstOrDefaultAsync(s => s.Token == token && !s.Revoked, ct);
    if (link is null || (link.ExpiresAt is { } e && e < DateTimeOffset.UtcNow)) return Results.NotFound();
    if (await BuildProfileAsync(db, link.StudentId, ct) is not { } p) return Results.NotFound();
    analytics.Capture("parent_profile_viewed", link.StudentId.ToString(), new Dictionary<string, object?> { ["source"] = "share_link" });
    return Results.Ok(p);
});

// ---------- Внутренний приём онлайн-партий от GameServer ----------
app.MapPost("/internal/games/archive", async (ArchiveGameRequest req, HttpRequest http,
    GameArchiver archiver, CancellationToken ct) =>
{
    if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();
    var created = await archiver.ArchiveOnlineAsync(req, ct);
    return Results.Ok(new { archived = created });
});

// Entitlement подписки для потребителей (Arena/Web) — server-to-server по внутреннему ключу.
app.MapGet("/internal/subscriptions/{sub}", async (string sub, HttpRequest http,
    SubscriptionService subs, CancellationToken ct) =>
{
    if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();
    return Results.Ok(await subs.GetAsync(sub, ct));
});

// Customer Portal: сессия hosted-портала провайдера (отмена/смена карты) для пользователя.
app.MapGet("/internal/subscriptions/{sub}/portal", async (string sub, HttpRequest http,
    SubscriptionService subsSvc, IBillingProvider billing, CancellationToken ct) =>
{
    if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();
    var customerId = await subsSvc.GetProviderCustomerIdAsync(sub, ct);
    var url = string.IsNullOrEmpty(customerId) ? null : await billing.CreatePortalUrlAsync(customerId, ct);
    return Results.Ok(new PortalLinkDto(url));
});

// Вытягивание статуса (reconcile из API провайдера, если вебхук не дошёл) по сохранённой подписке.
app.MapPost("/internal/subscriptions/{sub}/refresh", async (string sub, HttpRequest http,
    SubscriptionService subsSvc, IBillingProvider billing, CancellationToken ct) =>
{
    if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();
    var subId = await subsSvc.GetProviderSubscriptionIdAsync(sub, ct);
    if (!string.IsNullOrEmpty(subId) && await billing.FetchSubscriptionAsync(subId, ct) is { } state)
        await subsSvc.ReconcileAsync(state, ct);
    return Results.Ok(await subsSvc.GetAsync(sub, ct));
});

// Reconcile по transaction id из success-URL checkout — активирует премиум без вебхука (user_sub из txn).
app.MapPost("/internal/subscriptions/reconcile-transaction", async (ReconcileTxnRequest req, HttpRequest http,
    SubscriptionService subsSvc, IBillingProvider billing, CancellationToken ct) =>
{
    if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();
    var state = await billing.FetchByTransactionAsync(req.TransactionId, ct);
    if (state is not null) await subsSvc.ReconcileAsync(state, ct);
    return Results.Ok(state is null ? null : await subsSvc.GetAsync(state.UserSub, ct));
});

// Dev-активация премиума без провайдера (только Development) — локальный тест гейтинга.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/internal/subscriptions/dev-activate", async (DevActivateRequest req, HttpRequest http,
        SubscriptionService subs, CancellationToken ct) =>
    {
        if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();
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

internal sealed record ResolvedUser(string Sub, string DisplayName);

namespace ChessSchool.ApiService
{
    /// <summary>Маркерный тип для WebApplicationFactory в тестах (уникальное имя, без конфликта с Program).</summary>
    public sealed class ApiServiceMarker;
}
