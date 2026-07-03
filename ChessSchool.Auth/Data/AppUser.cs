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
}

/// <summary>Назначение одноразового e-mail-токена (ссылка из письма).</summary>
public enum EmailTokenPurpose
{
    ConfirmEmail = 0,
    ResetPassword = 1,
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
