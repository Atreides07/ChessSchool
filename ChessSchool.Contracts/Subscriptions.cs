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

/// <summary>Запрос reconcile по transaction id из success-URL checkout (`_ptxn`).</summary>
public sealed record ReconcileTxnRequest(string TransactionId);

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

// ---------------- Админка управления подписками ----------------

/// <summary>
/// Строка подписки для админки. Кроме состояния несёт e-mail/имя пользователя (резолвятся в IdP по sub
/// для человекочитаемого списка) — могут быть null, если пользователь не найден (удалён/чужой sub).
/// </summary>
public sealed record AdminSubscriptionDto(
    string UserSub,
    string? Email,
    string? DisplayName,
    SubscriptionStatus Status,
    string? Plan,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset UpdatedAt,
    string? ProviderSubscriptionId,   // есть → подписка заведена провайдером (Paddle), нет → ручная выдача
    bool IsPremium);

/// <summary>Админ-операция: задать подписку конкретному пользователю по его sub.</summary>
public sealed record AdminSetSubscriptionRequest(
    SubscriptionStatus Status,
    string? Plan,
    DateTimeOffset? CurrentPeriodEnd);

/// <summary>Админ-операция: задать подписку по e-mail (sub резолвится в IdP). Удобно для теста/поддержки.</summary>
public sealed record AdminSetByEmailRequest(
    string Email,
    SubscriptionStatus Status,
    string? Plan,
    DateTimeOffset? CurrentPeriodEnd);

/// <summary>Батч-резолв sub → профиль в IdP (для человекочитаемого списка подписок в админке).</summary>
public sealed record BySubsRequest(IReadOnlyList<string> Subs);
