namespace ChessSchool.Contracts;

/// <summary>Регистрация нового пользователя в IdP.</summary>
public sealed record RegisterRequest(string Email, string Password, string DisplayName);

/// <summary>Вход по email/паролю.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>Ответ IdP с токенами (OAuth2-подобный).</summary>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken);

/// <summary>Профиль пользователя из IdP.</summary>
public sealed record UserInfo(string Sub, string Email, string DisplayName);
