using ChessSchool.Contracts;

namespace ChessSchool.ApiService.Services.Billing;

/// <summary>
/// Провайдер эквайринга за интерфейсом (прод — Paddle, dev — заглушка). Карты у нас не ходят —
/// оплата идёт через hosted Checkout/Portal провайдера. Выбор реализации — по конфигу (наличие
/// Paddle:ApiKey), как S3↔MinIO в проекте. Здесь — операции, нужные клиенту для запуска оплаты и
/// самоуправления; разбор вебхуков делает PaddleBillingProvider (фаза 2).
/// </summary>
public interface IBillingProvider
{
    string Name { get; }

    /// <summary>Данные для запуска оплаты на клиенте. Dev-заглушка отдаёт DevAutoActivate=true
    /// (премиум включается без реальной оплаты — локальный путь).</summary>
    BillingCheckout CreateCheckout(string userSub, string plan);

    /// <summary>URL hosted Customer Portal (отмена/смена карты) для клиента провайдера. null — недоступно
    /// (нет customer id / dev-заглушка / провайдер недоступен).</summary>
    Task<string?> CreatePortalUrlAsync(string providerCustomerId, CancellationToken ct = default);

    /// <summary>Вытягивает текущее состояние подписки из API провайдера (reconcile, если вебхук не дошёл).
    /// fallbackUserSub — известный sub пользователя (из транзакции/нашей строки), подставляется, если в
    /// custom_data самой подписки его нет (Paddle хранит customData checkout-а на транзакции, не на подписке).</summary>
    Task<BillingEventDto?> FetchSubscriptionAsync(string providerSubscriptionId, CancellationToken ct = default,
        string? fallbackUserSub = null);

    /// <summary>Вытягивает состояние по transaction id из success-URL checkout (первичная активация без вебхука).</summary>
    Task<BillingEventDto?> FetchByTransactionAsync(string transactionId, CancellationToken ct = default);

    /// <summary>Находит подписку клиента по его e-mail (надёжное восстановление статуса: работает, даже если
    /// у нас нет строки подписки и не сохранён txn). userSub — связать найденную подписку с пользователем.</summary>
    Task<BillingEventDto?> FetchByCustomerEmailAsync(string email, string userSub, CancellationToken ct = default);
}

/// <summary>Параметры запуска checkout на клиенте (Paddle.js v2) либо dev-автоактивация.</summary>
public sealed record BillingCheckout(
    string Provider,
    bool DevAutoActivate,
    string? ClientToken = null,   // Paddle client-side token
    string? PriceId = null,       // Paddle price id выбранного плана
    string? CustomData = null,    // передаём userSub в Paddle, чтобы связать подписку с пользователем
    string? Environment = null);  // "sandbox" | "production"

/// <summary>Dev-заглушка: «оплата» проходит локально без провайдера (DevAutoActivate).</summary>
public sealed class DevStubBillingProvider : IBillingProvider
{
    public string Name => "dev-stub";

    public BillingCheckout CreateCheckout(string userSub, string plan) =>
        new(Name, DevAutoActivate: true, CustomData: userSub);

    public Task<string?> CreatePortalUrlAsync(string providerCustomerId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null); // dev: портала нет

    public Task<BillingEventDto?> FetchSubscriptionAsync(string providerSubscriptionId, CancellationToken ct = default,
        string? fallbackUserSub = null) =>
        Task.FromResult<BillingEventDto?>(null); // dev: вытягивать нечего (активация — кнопкой dev-activate)

    public Task<BillingEventDto?> FetchByTransactionAsync(string transactionId, CancellationToken ct = default) =>
        Task.FromResult<BillingEventDto?>(null);

    public Task<BillingEventDto?> FetchByCustomerEmailAsync(string email, string userSub, CancellationToken ct = default) =>
        Task.FromResult<BillingEventDto?>(null);
}
