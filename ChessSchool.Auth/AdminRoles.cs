namespace ChessSchool.Auth;

/// <summary>
/// Ролевая модель админов. IdP — источник истины: для админских e-mail в токен кладётся claim role=admin,
/// потребители (Arena) гейтят админку по этой роли (RequireRole). По умолчанию админ —
/// <see cref="DefaultAdminEmail"/>; список расширяется конфигом <c>Admin:Emails</c> (через запятую).
/// </summary>
public static class AdminRoles
{
    public const string Role = "admin";
    public const string DefaultAdminEmail = "akhmed@outlook.com";

    /// <summary>Множество админских e-mail из конфига; если конфиг пуст — дефолтный админ.</summary>
    public static HashSet<string> Resolve(string? configuredEmails)
    {
        var set = (configuredEmails ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) set.Add(DefaultAdminEmail); // по умолчанию админ — akhmed@outlook.com
        return set;
    }

    public static bool IsAdmin(IReadOnlySet<string> admins, string? email) =>
        !string.IsNullOrWhiteSpace(email) && admins.Contains(email);
}
