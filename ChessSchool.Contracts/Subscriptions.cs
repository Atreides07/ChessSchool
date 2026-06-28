namespace ChessSchool.Contracts;

/// <summary>Статус подписки игрока (B2C-премиум). Маппится из статусов провайдера (Paddle).</summary>
public enum SubscriptionStatus
{
    None = 0,       // подписки нет
    Trialing = 1,   // пробный период
    Active = 2,     // оплачена и активна
    PastDue = 3,    // просрочен платёж (доступ до конца оплаченного периода — ретенция)
    Paused = 4,     // приостановлена
    Canceled = 5,   // отменена/истекла
}

/// <summary>
/// Состояние подписки пользователя для потребителей (Arena/Web). IsPremium — единственный флаг,
/// по которому гейтить фичи: считается на сервере из статуса и срока периода, клиенту не доверяем.
/// </summary>
public sealed record SubscriptionDto(
    string UserSub,
    SubscriptionStatus Status,
    string? Plan,
    DateTimeOffset? CurrentPeriodEnd,
    bool IsPremium);

/// <summary>Запрос dev-активации премиума (только Development, для локального теста без провайдера).</summary>
public sealed record DevActivateRequest(string UserSub, string? Plan = null);

/// <summary>Ссылка на hosted Customer Portal провайдера (отмена/смена карты). Url=null — недоступно.</summary>
public sealed record PortalLinkDto(string? Url);

/// <summary>Нормализованное событие биллинга (из вебхука провайдера или dev-активации) → состояние.</summary>
public sealed record BillingEventDto(
    string EventId,                       // id события провайдера — идемпотентность
    string UserSub,
    SubscriptionStatus Status,
    string? Plan = null,
    string? ProviderSubscriptionId = null,
    string? ProviderCustomerId = null,
    string? PriceId = null,
    DateTimeOffset? CurrentPeriodEnd = null);
