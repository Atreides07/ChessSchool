namespace ChessSchool.Auth.Data;

/// <summary>Учётная запись пользователя IdP (общая для всех продуктов).</summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Подтверждён ли e-mail (переход по ссылке из письма). Логин запрещён до подтверждения.</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>
    /// Запрошенный новый адрес при смене ПОДТВЕРЖДЁННОГО e-mail (verify-new-before-switch): пока владение
    /// новым адресом не доказано переходом по ссылке, основной <see cref="Email"/> не меняется. На confirm
    /// значение переезжает в <see cref="Email"/> и обнуляется. null — смена не в процессе.
    /// </summary>
    public string? PendingEmail { get; set; }

    /// <summary>
    /// Метка безопасности: значение попадает в claim cookie-сессии; при смене пароля (или иной чувствительной
    /// операции) перевыпускается, из-за чего ВСЕ ранее выданные cookie-сессии на всех устройствах перестают
    /// проходить валидацию (см. OnValidatePrincipal) — мгновенный логаут везде. Дополняет отзыв OIDC-токенов.
    /// </summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>Назначение одноразового e-mail-токена (ссылка из письма).</summary>
public enum EmailTokenPurpose
{
    ConfirmEmail = 0,
    ResetPassword = 1,
    ChangeEmail = 2, // подтверждение нового адреса при смене подтверждённого e-mail (verify-new-before-switch)
}

/// <summary>
/// Одноразовый токен из письма (подтверждение e-mail / сброс пароля). В БД хранится ТОЛЬКО хэш токена
/// (SHA-256) — сырой токен живёт лишь в ссылке письма, поэтому утечка БД не даёт подделать переход.
/// </summary>
public class EmailToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public EmailTokenPurpose Purpose { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Тип аудируемого события аутентификации (для наблюдаемости и детекта аномалий).</summary>
public enum AuthEventType
{
    LoginSuccess = 0,
    LoginFailure = 1,
    Register = 2,
    EmailConfirmed = 3,
    ConfirmationResent = 4,
    EmailChanged = 5,
    PasswordResetRequested = 6,
    PasswordReset = 7,
    NewDeviceLogin = 8, // успешный вход с ранее не виденного IP (уведомили владельца)
}

/// <summary>
/// Запись аудита auth-события (вход/фейл/регистрация/подтверждение/смена e-mail/сброс пароля). Пишется в
/// общий стор (PostgreSQL) — виден всем нодам. Секретов не содержит (пароли/сырые токены сюда не попадают);
/// e-mail хранится для расследования инцидентов. IP/UA — для детекта аномалий (перебор, вход с нового места).
/// </summary>
public class AuthEvent
{
    public long Id { get; set; }
    public AuthEventType Type { get; set; }
    public Guid? UserId { get; set; }
    public string? Email { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>Необязательная деталь события (напр. причина фейла), без секретов.</summary>
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Opaque refresh-токен с ротацией.</summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Token { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Revoked { get; set; }
}

/// <summary>Одноразовый authorization code (OAuth2 code flow) для SSO между приложениями.</summary>
public class AuthCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }
}
