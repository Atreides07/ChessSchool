using ChessSchool.Auth.Data;
using ChessSchool.Auth.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;

namespace ChessSchool.Auth;

/// <summary>
/// Эндпоинты двухфакторной аутентификации (TOTP): настройка/включение/отключение и ввод второго фактора
/// при входе. Логика прежняя — вынесена из Program.cs в группу ради читаемости.
/// </summary>
public static class MfaEndpoints
{
    public static void MapMfaEndpoints(this WebApplication app, AuthConfig cfg)
    {
        // ---------------- MFA (TOTP): настройка ----------------
        app.MapGet("/account/mfa", async (HttpContext ctx, AuthDbContext db, MfaService mfa, string? @return, string? error, string? required) =>
        {
            var auth = await ctx.AuthenticateAsync("idp");
            var user = Guid.TryParse(auth.Principal?.FindFirst("sub")?.Value, out var id) ? await db.Users.FindAsync(id) : null;
            if (user is null) return Results.Redirect($"/account/login?return={Uri.EscapeDataString(@return ?? "/")}");

            // `required` берём как строку: редиректы шлют "1", а bool-биндинг minimal-API парсит лишь true/false —
            // "1" валило страницу в 500 (BadHttpRequestException). Параметр не должен ронять страницу: трактуем truthy.
            bool requiredFlag = required is "1" || string.Equals(required, "true", StringComparison.OrdinalIgnoreCase);
            // Для админа 2FA обязательна — показываем требование даже без ?required (напр. открыл страницу сам).
            var mustEnable = requiredFlag || (cfg.RequireMfaForAdmins && AdminRoles.IsAdmin(cfg.AdminEmails, user.Email));

            if (user.MfaEnabled)
                return Results.Content(AccountPages.MfaSettingsPage(true, null, null, @return ?? "/", error, mustEnable), "text/html; charset=utf-8");

            // Настройка: генерируем свежий секрет, сохраняем (зашифрованно, MfaEnabled=false), показываем для сканирования.
            var secret = Totp.GenerateSecret();
            user.MfaSecret = mfa.Protect(secret);
            await db.SaveChangesAsync();
            var uri = Totp.OtpAuthUri(MfaService.Issuer, user.Email, secret);
            return Results.Content(AccountPages.MfaSettingsPage(false, Base32.Encode(secret), uri, @return ?? "/", error, mustEnable), "text/html; charset=utf-8");
        });

        app.MapPost("/account/mfa/enable", async (HttpContext ctx, AuthDbContext db, MfaService mfa, AuthAudit audit) =>
        {
            var auth = await ctx.AuthenticateAsync("idp");
            var user = Guid.TryParse(auth.Principal?.FindFirst("sub")?.Value, out var id) ? await db.Users.FindAsync(id) : null;
            var form = await ctx.Request.ReadFormAsync();
            string ret = form["return"].ToString();
            if (user is null) return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}");
            if (user.MfaEnabled) return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);

            // Подтверждаем владение: код из приложения должен сойтись с только что сохранённым секретом.
            if (string.IsNullOrEmpty(user.MfaSecret) || !mfa.VerifyTotp(user, form["code"], DateTimeOffset.UtcNow))
                return Results.Redirect($"/account/mfa?return={Uri.EscapeDataString(ret)}&error=code");

            user.MfaEnabled = true;
            await db.SaveChangesAsync();
            var codes = await mfa.ResetRecoveryCodesAsync(user.Id);
            await audit.LogAsync(ctx, AuthEventType.MfaEnabled, user.Email, user.Id);
            return Results.Content(AccountPages.MfaRecoveryCodesPage(codes, ret), "text/html; charset=utf-8");
        }).RequireRateLimiting("auth"); // анти-перебор кода подтверждения

        app.MapPost("/account/mfa/disable", async (HttpContext ctx, AuthDbContext db, MfaService mfa, AuthAudit audit) =>
        {
            var auth = await ctx.AuthenticateAsync("idp");
            var user = Guid.TryParse(auth.Principal?.FindFirst("sub")?.Value, out var id) ? await db.Users.FindAsync(id) : null;
            var form = await ctx.Request.ReadFormAsync();
            string ret = form["return"].ToString();
            if (user is null) return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}");
            if (!user.MfaEnabled) return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
            // Админам отключать MFA нельзя, когда она обязательна (иначе окно без 2FA до гейта в authorize).
            if (cfg.RequireMfaForAdmins && AdminRoles.IsAdmin(cfg.AdminEmails, user.Email))
                return Results.Redirect($"/account/mfa?return={Uri.EscapeDataString(ret)}&error=adminlock");

            // Отключение — чувствительно: требуем действующий код (TOTP или резервный).
            var code = form["code"].ToString();
            var ok = mfa.VerifyTotp(user, code, DateTimeOffset.UtcNow) || await mfa.ConsumeRecoveryCodeAsync(user.Id, code);
            if (!ok) return Results.Redirect($"/account/mfa?return={Uri.EscapeDataString(ret)}&error=code");

            user.MfaEnabled = false;
            user.MfaSecret = null;
            await db.SaveChangesAsync();
            await mfa.ClearRecoveryCodesAsync(user.Id);
            await audit.LogAsync(ctx, AuthEventType.MfaDisabled, user.Email, user.Id);
            return Results.Redirect($"/account/mfa?return={Uri.EscapeDataString(ret)}");
        }).RequireRateLimiting("auth");

        // ---------------- MFA: второй фактор при входе ----------------
        app.MapGet("/account/mfa/verify", (HttpContext ctx, string? @return, string? error) =>
        {
            if (AccountFlow.ReadMfaPendingUser(ctx) is null) // нет валидного pending-маркера → на обычный вход
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(@return ?? "/")}");
            return Results.Content(AccountPages.MfaVerifyPage(@return ?? "/", error), "text/html; charset=utf-8");
        });

        app.MapPost("/account/mfa/verify", async (HttpContext ctx, AuthDbContext db, MfaService mfa, AuthAudit audit, IEmailSender emailSender) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            string ret = form["return"].ToString();
            var uid = AccountFlow.ReadMfaPendingUser(ctx);
            var user = uid is { } id ? await db.Users.FindAsync(id) : null;
            if (user is null || !user.MfaEnabled)
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}");

            var code = form["code"].ToString();
            var ok = mfa.VerifyTotp(user, code, DateTimeOffset.UtcNow) || await mfa.ConsumeRecoveryCodeAsync(user.Id, code);
            if (!ok)
            {
                await audit.LogAsync(ctx, AuthEventType.MfaChallengeFailed, user.Email, user.Id);
                return Results.Redirect($"/account/mfa/verify?return={Uri.EscapeDataString(ret)}&error=1");
            }

            AccountFlow.ClearMfaPendingCookie(ctx);
            await AccountFlow.CompleteLoginAsync(ctx, db, audit, emailSender, user);
            return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
        }).RequireRateLimiting("auth"); // анти-перебор второго фактора
    }
}
