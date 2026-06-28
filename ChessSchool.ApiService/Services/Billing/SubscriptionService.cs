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
}
