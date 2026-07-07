namespace ChessSchool.Auth;

/// <summary>
/// Резолвнутая на старте конфигурация auth-эндпоинтов (значения из конфигурации/окружения), которую
/// раньше эндпоинты захватывали как локальные переменные Program.cs. Прокидывается в группы эндпоинтов
/// (<c>Map*Endpoints</c>), чтобы вынести wiring из одного гигантского файла без изменения поведения.
/// </summary>
/// <param name="MinPasswordLength">NIST-минимум длины пароля (регистрация/сброс/подписи форм).</param>
/// <param name="CheckPwned">Проверять пароль по базе утечек HIBP (в тестах выключено).</param>
/// <param name="DummyPasswordHash">Фиктивный хэш для constant-time логина (анти-энумерация по таймингу).</param>
/// <param name="AdminEmails">Множество админских e-mail (источник истины — конфиг Admin:Emails).</param>
/// <param name="RequireMfaForAdmins">Обязательная 2FA для админов (гейт в authorize + форс на входе).</param>
public sealed record AuthConfig(
    int MinPasswordLength,
    bool CheckPwned,
    string DummyPasswordHash,
    IReadOnlySet<string> AdminEmails,
    bool RequireMfaForAdmins);
