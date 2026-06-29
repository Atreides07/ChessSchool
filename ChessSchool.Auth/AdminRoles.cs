namespace ChessSchool.Auth;

/// <summary>
/// Ролевая модель админов. IdP — источник истины: для админских e-mail в токен кладётся claim role=admin,
/// потребители (Arena) гейтят админку по этой роли (RequireRole). Кто админ — задаётся ТОЛЬКО конфигом
/// <c>Admin:Emails</c> (через запятую): локально — user-secrets, в проде — env/KMS. В коде e-mail нет
/// (никакой PII в git, смена админов без передеплоя). Пустой список ⇒ админов нет (в проде закрыто).
/// </summary>
public static class AdminRoles
{
    public const string Role = "admin";

    /// <summary>Множество админских e-mail из конфига (регистронезависимо). Пусто ⇒ админов нет.</summary>
    public static HashSet<string> Resolve(string? configuredEmails) =>
        (configuredEmails ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsAdmin(IReadOnlySet<string> admins, string? email) =>
        !string.IsNullOrWhiteSpace(email) && admins.Contains(email);
}
