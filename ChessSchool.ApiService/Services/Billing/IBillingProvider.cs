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
}
