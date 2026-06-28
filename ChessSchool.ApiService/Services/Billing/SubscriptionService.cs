using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Domain;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.ApiService.Services.Billing;

/// <summary>
/// Применяет нормализованные события биллинга к состоянию подписки и отдаёт entitlement.
/// Идемпотентно (повтор события/несколько нод не двоят), источник истины — Postgres.
/// </summary>
public sealed class SubscriptionService(SchoolDbContext db, ILogger<SubscriptionService> logger)
{
    /// <summary>Применить событие. true — применили, false — уже обработано (идемпотентность).</summary>
    public async Task<bool> ApplyAsync(BillingEventDto e, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(e.EventId) || string.IsNullOrWhiteSpace(e.UserSub)) return false;

        if (await db.ProcessedBillingEvents.AnyAsync(p => p.EventId == e.EventId, ct))
            return false; // уже обработано

        db.ProcessedBillingEvents.Add(new ProcessedBillingEvent { EventId = e.EventId });
        await UpsertAsync(e, ct);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Гонка: другое событие с тем же EventId записалось между проверкой и сохранением
            // (PK ProcessedBillingEvents). Считаем уже обработанным — идемпотентность сохранена.
            logger.LogDebug(ex, "Событие биллинга {EventId} уже обработано (гонка).", e.EventId);
            return false;
        }
    }

    /// <summary>
    /// «Вытягивание»: применяет состояние, полученное напрямую из API провайдера (если вебхук не дошёл/
    /// опоздал). Без дедупа по EventId — всегда отражаем актуальное состояние (upsert, last-write-wins).
    /// </summary>
    public async Task ReconcileAsync(BillingEventDto state, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state.UserSub)) return;
        await UpsertAsync(state, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertAsync(BillingEventDto e, CancellationToken ct)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserSub == e.UserSub, ct);
        if (sub is null)
        {
            sub = new Subscription { UserSub = e.UserSub };
            db.Subscriptions.Add(sub);
        }
        sub.Status = e.Status;
        sub.Plan = e.Plan ?? sub.Plan;
        sub.ProviderSubscriptionId = e.ProviderSubscriptionId ?? sub.ProviderSubscriptionId;
        sub.ProviderCustomerId = e.ProviderCustomerId ?? sub.ProviderCustomerId;
        sub.PriceId = e.PriceId ?? sub.PriceId;
        sub.CurrentPeriodEnd = e.CurrentPeriodEnd ?? sub.CurrentPeriodEnd;
        sub.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Id подписки у провайдера (для reconcile по сохранённой подписке). null — нет подписки.</summary>
    public Task<string?> GetProviderSubscriptionIdAsync(string userSub, CancellationToken ct = default) =>
        db.Subscriptions.AsNoTracking().Where(s => s.UserSub == userSub)
            .Select(s => s.ProviderSubscriptionId).FirstOrDefaultAsync(ct);

    public async Task<SubscriptionDto> GetAsync(string userSub, CancellationToken ct = default)
    {
        var s = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.UserSub == userSub, ct);
        return s is null
            ? new SubscriptionDto(userSub, SubscriptionStatus.None, null, null, IsPremium: false)
            : new SubscriptionDto(userSub, s.Status, s.Plan, s.CurrentPeriodEnd, IsPremium(s.Status, s.CurrentPeriodEnd));
    }

    /// <summary>Id клиента у провайдера (для создания сессии Customer Portal). null — нет подписки/клиента.</summary>
    public Task<string?> GetProviderCustomerIdAsync(string userSub, CancellationToken ct = default) =>
        db.Subscriptions.AsNoTracking().Where(s => s.UserSub == userSub)
            .Select(s => s.ProviderCustomerId).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Даёт ли подписка премиум прямо сейчас. Active/Trialing/PastDue (ретенция — доступ до конца
    /// оплаченного периода) и период не истёк. Это единственное место, по которому гейтятся фичи.
    /// </summary>
    public static bool IsPremium(SubscriptionStatus status, DateTimeOffset? periodEnd)
    {
        bool grants = status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue;
        return grants && (periodEnd is null || periodEnd > DateTimeOffset.UtcNow);
    }

    // ---------------- Админ-операции (ручное управление/тест, в обход провайдера) ----------------

    /// <summary>
    /// Список подписок для админки (последние изменённые сверху, с лимитом). Без e-mail/имени —
    /// их добивает вызывающий код, резолвя sub в IdP (это не задача стора подписок).
    /// </summary>
    public async Task<IReadOnlyList<AdminSubscriptionDto>> ListAsync(int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var rows = await db.Subscriptions.AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(s => new AdminSubscriptionDto(
            s.UserSub, Email: null, DisplayName: null, s.Status, s.Plan,
            s.CurrentPeriodEnd, s.UpdatedAt, s.ProviderSubscriptionId,
            IsPremium(s.Status, s.CurrentPeriodEnd))).ToList();
    }

    /// <summary>
    /// Админ задаёт состояние подписки напрямую (выдать/снять премиум, подвинуть срок для теста).
    /// Статус и срок ставятся явно (в т.ч. срок можно очистить или поставить в прошлое — «истекло»),
    /// в отличие от upsert событий биллинга, который пустые поля сохраняет. last-write-wins.
    /// </summary>
    public async Task<SubscriptionDto> AdminSetAsync(string userSub, SubscriptionStatus status,
        string? plan, DateTimeOffset? periodEnd, CancellationToken ct = default)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserSub == userSub, ct);
        if (sub is null)
        {
            sub = new Subscription { UserSub = userSub };
            db.Subscriptions.Add(sub);
        }
        sub.Status = status;
        sub.Plan = string.IsNullOrWhiteSpace(plan) ? sub.Plan : plan;
        sub.CurrentPeriodEnd = periodEnd;
        sub.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(userSub, ct);
    }

    /// <summary>Админ полностью удаляет подписку пользователя (премиум снимается). true — была и удалена.</summary>
    public async Task<bool> AdminRemoveAsync(string userSub, CancellationToken ct = default)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserSub == userSub, ct);
        if (sub is null) return false;
        db.Subscriptions.Remove(sub);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
