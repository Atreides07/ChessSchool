using System.Security.Claims;
using ChessSchool.Auth.Data;
using ChessSchool.Auth.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace ChessSchool.Auth;

/// <summary>
/// Общие шаги аутентификации, дёргаемые из нескольких эндпоинтов: выпуск cookie-сессии IdP, полное
/// завершение входа (аудит + уведомление о новом устройстве), pending-маркер MFA между шагами входа,
/// письмо подтверждения e-mail и маппинг claim'ов на назначения токенов. Вынесено из Program.cs,
/// чтобы файл входа/wiring не тонул в хелперах. Поведение не меняется.
/// </summary>
public static class AccountFlow
{
    public static async Task SignInCookieAsync(HttpContext ctx, AppUser user)
    {
        var identity = new ClaimsIdentity("idp");
        identity.AddClaim(new Claim("sub", user.Id.ToString()));
        identity.AddClaim(new Claim("name", user.DisplayName));
        identity.AddClaim(new Claim("email", user.Email));
        identity.AddClaim(new Claim("email_verified", user.EmailConfirmed ? "true" : "false"));
        identity.AddClaim(new Claim("sstamp", user.SecurityStamp)); // метка для мгновенной инвалидации сессий
        await ctx.SignInAsync("idp", new ClaimsPrincipal(identity));
    }

    // Полное завершение входа: cookie-сессия + аудит успеха + уведомление о входе с нового устройства.
    // Общая точка для пути без MFA и для пути после успешного второго фактора.
    public static async Task CompleteLoginAsync(HttpContext ctx, AuthDbContext db, AuthAudit audit, IEmailSender emailSender, AppUser user)
    {
        // Новый IP? Проверяем ДО записи текущего LoginSuccess (иначе текущий IP сразу «известен»).
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var hadPriorLogins = await db.AuthEvents.AnyAsync(e => e.UserId == user.Id && e.Type == AuthEventType.LoginSuccess);
        var knownIp = ip is not null &&
            await db.AuthEvents.AnyAsync(e => e.UserId == user.Id && e.Type == AuthEventType.LoginSuccess && e.Ip == ip);

        await SignInCookieAsync(ctx, user);
        await audit.LogAsync(ctx, AuthEventType.LoginSuccess, user.Email, user.Id);

        if (hadPriorLogins && !knownIp)
        {
            var (subject, html) = EmailTemplates.NewSignIn(user.DisplayName, ip, IsEnCulture());
            await emailSender.SendAsync(user.Email, subject, html);
            await audit.LogAsync(ctx, AuthEventType.NewDeviceLogin, user.Email, user.Id, detail: ip);
        }
    }

    // ---- MFA pending-маркер: пароль пройден, ждём второй фактор (короткоживущий, DataProtection) ----
    private const string MfaPendingCookie = "idp_mfa";
    private static IDataProtector MfaPendingProtector(HttpContext ctx) =>
        ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("ChessSchool.Auth.Mfa.Pending.v1");

    public static void SetMfaPendingCookie(HttpContext ctx, Guid userId)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var payload = MfaPendingProtector(ctx).Protect($"{userId:N}|{expires.ToUnixTimeSeconds()}");
        ctx.Response.Cookies.Append(MfaPendingCookie, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = expires,
        });
    }

    public static Guid? ReadMfaPendingUser(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(MfaPendingCookie, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var parts = MfaPendingProtector(ctx).Unprotect(raw).Split('|');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var uid)) return null;
            if (!long.TryParse(parts[1], out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return null;
            return uid;
        }
        catch { return null; } // повреждённый/подделанный/старым ключом — считаем отсутствующим
    }

    public static void ClearMfaPendingCookie(HttpContext ctx) => ctx.Response.Cookies.Delete(MfaPendingCookie);

    // Выпускает токен подтверждения и шлёт письмо со ссылкой (абсолютный URL — по forwarded-хосту запроса).
    public static async Task SendConfirmationEmailAsync(HttpContext ctx, EmailTokenService tokens, IEmailSender email,
        AppUser user, string? ret)
    {
        var raw = await tokens.CreateAsync(user.Id, EmailTokenPurpose.ConfirmEmail, EmailTokenService.ConfirmLifetime);
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var link = $"{baseUrl}/account/confirm?token={Uri.EscapeDataString(raw)}&return={Uri.EscapeDataString(ret ?? "")}";
        var (subject, html) = EmailTemplates.ConfirmEmail(user.DisplayName, link, IsEnCulture());
        await email.SendAsync(user.Email, subject, html);
    }

    public static bool IsEnCulture() => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en";

    public static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
    {
        Claims.Name or Claims.Email or Claims.EmailVerified or Claims.Subject or Claims.Role => [Destinations.AccessToken, Destinations.IdentityToken],
        _ => [Destinations.AccessToken]
    };
}
