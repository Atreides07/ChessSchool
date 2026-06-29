using ChessSchool.Auth;

namespace ChessSchool.Tests;

/// <summary>
/// Ролевая модель админов в IdP: по умолчанию админ — akhmed@outlook.com, список расширяется конфигом
/// Admin:Emails (через запятую), сравнение e-mail регистронезависимо.
/// </summary>
public class AdminRolesTests
{
    [Fact]
    public void Resolve_EmptyConfig_NoAdmins_ClosedByDefault()
    {
        // E-mail в коде нет: пустой конфиг ⇒ админов нет (в проде доступ закрыт). Админ задаётся
        // только конфигом Admin:Emails (локально — user-secrets, в проде — env/KMS).
        foreach (var cfg in new[] { null, "", "   " })
        {
            var admins = AdminRoles.Resolve(cfg);
            Assert.Empty(admins);
            Assert.False(AdminRoles.IsAdmin(admins, "akhmed@outlook.com"));
        }
    }

    [Fact]
    public void Resolve_ConfiguredList_ParsedAndTrimmed()
    {
        var admins = AdminRoles.Resolve(" a@x.com , b@y.com ,, c@z.com ");
        Assert.Equal(3, admins.Count);
        Assert.True(AdminRoles.IsAdmin(admins, "a@x.com"));
        Assert.True(AdminRoles.IsAdmin(admins, "c@z.com"));
        Assert.False(AdminRoles.IsAdmin(admins, "nobody@x.com"));
    }

    [Fact]
    public void IsAdmin_IsCaseInsensitive_AndRejectsNonAdmins()
    {
        var admins = AdminRoles.Resolve("Admin@Example.com");
        Assert.True(AdminRoles.IsAdmin(admins, "admin@example.COM"));
        Assert.False(AdminRoles.IsAdmin(admins, "user@example.com"));
        Assert.False(AdminRoles.IsAdmin(admins, null));
        Assert.False(AdminRoles.IsAdmin(admins, ""));
    }
}
