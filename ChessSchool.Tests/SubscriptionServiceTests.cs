using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Services.Billing;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessSchool.Tests;

/// <summary>
/// Сервис подписок (B2C-премиум): идемпотентное применение событий биллинга, переходы статусов и
/// расчёт премиума (по статусу + сроку периода). Источник истины — БД, клиенту не доверяем.
/// </summary>
public class SubscriptionServiceTests
{
    private static SchoolDbContext NewDb() =>
        new(new DbContextOptionsBuilder<SchoolDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SubscriptionService Svc(SchoolDbContext db) => new(db, NullLogger<SubscriptionService>.Instance);

    [Fact]
    public async Task Activate_GrantsPremium()
    {
        using var db = NewDb();
        var svc = Svc(db);

        var applied = await svc.ApplyAsync(new BillingEventDto("evt-1", "user-1", SubscriptionStatus.Active,
            "premium", CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30)));

        Assert.True(applied);
        var dto = await svc.GetAsync("user-1");
        Assert.True(dto.IsPremium);
        Assert.Equal(SubscriptionStatus.Active, dto.Status);
        Assert.Equal("premium", dto.Plan);
    }

    [Fact]
    public async Task SameEventId_Twice_IsIdempotent()
    {
        using var db = NewDb();
        var svc = Svc(db);

        Assert.True(await svc.ApplyAsync(new BillingEventDto("evt-1", "u", SubscriptionStatus.Active, "premium",
            CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30))));
        // Повтор того же события (даже с другим статусом) игнорируется — состояние не меняется.
        Assert.False(await svc.ApplyAsync(new BillingEventDto("evt-1", "u", SubscriptionStatus.Canceled)));

        var dto = await svc.GetAsync("u");
        Assert.True(dto.IsPremium);
        Assert.Equal(1, await db.Subscriptions.CountAsync());
    }

    [Fact]
    public async Task Cancel_RevokesPremium()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.ApplyAsync(new BillingEventDto("e1", "u", SubscriptionStatus.Active, "premium",
            CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(30)));
        await svc.ApplyAsync(new BillingEventDto("e2", "u", SubscriptionStatus.Canceled));

        Assert.False((await svc.GetAsync("u")).IsPremium);
    }

    [Fact]
    public async Task Reconcile_AppliesLatestState_WithoutEventDedup()
    {
        using var db = NewDb();
        var svc = Svc(db);
        // «Вытягивание»: применяем состояние из API напрямую (вебхука не было).
        await svc.ReconcileAsync(new BillingEventDto("ignored", "u", SubscriptionStatus.Active, "premium",
            CurrentPeriodEnd: DateTimeOffset.UtcNow.AddDays(5)));
        Assert.True((await svc.GetAsync("u")).IsPremium);

        // Повторный reconcile меняет статус (дедупа по EventId нет — всегда актуальное состояние).
        await svc.ReconcileAsync(new BillingEventDto("ignored", "u", SubscriptionStatus.Canceled));
        Assert.False((await svc.GetAsync("u")).IsPremium);
    }

    [Fact]
    public async Task UnknownUser_IsNotPremium()
    {
        using var db = NewDb();
        var dto = await Svc(db).GetAsync("nobody");
        Assert.Equal(SubscriptionStatus.None, dto.Status);
        Assert.False(dto.IsPremium);
    }

    [Fact]
    public async Task AdminSet_GrantsAndRevokes_AndShiftsExpiry()
    {
        using var db = NewDb();
        var svc = Svc(db);

        // Выдать вручную (в обход провайдера) — премиум активен.
        var granted = await svc.AdminSetAsync("u", SubscriptionStatus.Active, "premium",
            DateTimeOffset.UtcNow.AddDays(30));
        Assert.True(granted.IsPremium);
        Assert.Equal(SubscriptionStatus.Active, granted.Status);

        // Подвинуть срок в прошлое (тест «истекло») — премиум снимается, хотя статус активен.
        var expired = await svc.AdminSetAsync("u", SubscriptionStatus.Active, null,
            DateTimeOffset.UtcNow.AddDays(-1));
        Assert.False(expired.IsPremium);
        Assert.Equal("premium", expired.Plan); // план сохранён (пустой plan не затирает)

        // Бессрочно (срок очищен) — снова премиум.
        var forever = await svc.AdminSetAsync("u", SubscriptionStatus.Active, null, null);
        Assert.True(forever.IsPremium);
        Assert.Null(forever.CurrentPeriodEnd);

        Assert.Equal(1, await db.Subscriptions.CountAsync()); // одна строка на пользователя (upsert)
    }

    [Fact]
    public async Task AdminRemove_DeletesSubscription()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.AdminSetAsync("u", SubscriptionStatus.Active, "premium", null);

        Assert.True(await svc.AdminRemoveAsync("u"));
        Assert.False((await svc.GetAsync("u")).IsPremium);
        Assert.Equal(0, await db.Subscriptions.CountAsync());
        Assert.False(await svc.AdminRemoveAsync("u")); // повтор — уже нет
    }

    [Fact]
    public async Task List_ReturnsRows_NewestFirst_WithoutUserInfo()
    {
        using var db = NewDb();
        var svc = Svc(db);
        await svc.AdminSetAsync("old", SubscriptionStatus.Canceled, "premium", null);
        await svc.AdminSetAsync("new", SubscriptionStatus.Active, "premium", DateTimeOffset.UtcNow.AddDays(10));

        var rows = await svc.ListAsync(50);
        Assert.Equal(2, rows.Count);
        Assert.Equal("new", rows[0].UserSub); // последняя изменённая — сверху
        Assert.True(rows[0].IsPremium);
        Assert.Null(rows[0].Email); // e-mail добивает вызывающий код (резолв в IdP), не стор
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, 1, true)]
    [InlineData(SubscriptionStatus.Trialing, 1, true)]
    [InlineData(SubscriptionStatus.PastDue, 1, true)]    // ретенция: доступ до конца оплаченного периода
    [InlineData(SubscriptionStatus.Active, -1, false)]   // период истёк
    [InlineData(SubscriptionStatus.Canceled, 1, false)]
    [InlineData(SubscriptionStatus.Paused, 1, false)]
    [InlineData(SubscriptionStatus.None, 1, false)]
    public void IsPremium_Matrix(SubscriptionStatus status, int daysOffset, bool expected)
        => Assert.Equal(expected, SubscriptionService.IsPremium(status, DateTimeOffset.UtcNow.AddDays(daysOffset)));
}
