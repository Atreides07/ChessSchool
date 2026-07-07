using ChessSchool.Auth.Data;
using ChessSchool.Auth.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace ChessSchool.Auth;

/// <summary>
/// Эндпоинты аккаунта (cookie-сессия IdP): вход/регистрация, подтверждение и переотправка письма,
/// управление/смена e-mail, сброс пароля. Логика прежняя — вынесена из Program.cs в группу ради читаемости.
/// </summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app, AuthConfig cfg)
    {
        // ---------------- Страница входа / регистрации (cookie-сессия IdP) ----------------
        app.MapGet("/account/login", (string? @return, string? error, string? mode, string? email) =>
            Results.Content(AccountPages.LoginPage(@return ?? "/", error, mode == "register", mode == "sent", email, cfg.MinPasswordLength),
                "text/html; charset=utf-8"));

        app.MapPost("/account/login", async (HttpContext ctx, AuthDbContext db, IPasswordHasher<AppUser> hasher, AuthAudit audit, IEmailSender emailSender) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            string email = form["email"].ToString().Trim().ToLowerInvariant();
            string ret = form["return"].ToString();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null)
            {
                hasher.VerifyHashedPassword(new AppUser(), cfg.DummyPasswordHash, form["password"]!); // выравниваем тайминг
                await audit.LogAsync(ctx, AuthEventType.LoginFailure, email, detail: "no-user");
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1");
            }
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, form["password"]!) == PasswordVerificationResult.Failed)
            {
                await audit.LogAsync(ctx, AuthEventType.LoginFailure, email, user.Id, "bad-password");
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1");
            }

            // MFA включена → пароль пройден, но полноценно НЕ логиним: ставим короткоживущий pending-маркер
            // (DataProtection, 5 мин) и уводим на ввод второго фактора. Полный вход — только после TOTP/recovery.
            if (user.MfaEnabled)
            {
                AccountFlow.SetMfaPendingCookie(ctx, user.Id);
                return Results.Redirect($"/account/mfa/verify?return={Uri.EscapeDataString(ret)}");
            }

            await AccountFlow.CompleteLoginAsync(ctx, db, audit, emailSender, user);

            // Админ без MFA (когда она обязательна) → форсим настройку: сессия есть (нужна для enrollment),
            // но реальный доступ к приложениям гейтится в authorize до включения 2FA.
            if (cfg.RequireMfaForAdmins && !user.MfaEnabled && AdminRoles.IsAdmin(cfg.AdminEmails, user.Email))
                return Results.Redirect($"/account/mfa?required=1&return={Uri.EscapeDataString(ret)}");
            return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
        }).RequireRateLimiting("auth"); // защита от перебора пароля

        app.MapPost("/account/register", async (HttpContext ctx, AuthDbContext db, IPasswordHasher<AppUser> hasher,
            EmailTokenService tokens, IEmailSender email, IPwnedPasswordChecker pwned, AuthAudit audit, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            string em = form["email"].ToString().Trim().ToLowerInvariant();
            string ret = form["return"].ToString();
            string password = form["password"]!;
            if (string.IsNullOrWhiteSpace(em) || !em.Contains('@'))
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1&mode=register");
            if (!PasswordPolicy.IsAcceptable(password, cfg.MinPasswordLength, out _)) // NIST: решает длина, без композиции
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=weak&mode=register");

            var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == em);
            if (existing is not null)
            {
                // Уже подтверждён → e-mail занят, ведём на вход. Не подтверждён → переотправляем письмо.
                if (existing.EmailConfirmed)
                    return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=exists&mode=register");
                await AccountFlow.SendConfirmationEmailAsync(ctx, tokens, email, existing, ret);
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&mode=sent&email={Uri.EscapeDataString(em)}");
            }

            // Пароль не должен фигурировать в известных утечках (HIBP, k-anonymity). Недоступность HIBP → не блокируем.
            if (cfg.CheckPwned && await pwned.IsPwnedAsync(password, ct))
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=pwned&mode=register");

            // Регистрация: создаём НЕподтверждённого, шлём письмо и СРАЗУ пускаем (мягкий гейт) — ценность
            // доступна немедленно, подтверждение просим баннером; чувствительное закрыто до email_verified=true.
            var user = new AppUser { Email = em, DisplayName = form["name"].ToString() };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Гонка регистраций одним e-mail (TOCTOU): проверка existing выше и вставка не атомарны, поэтому
                // второй параллельный запрос проходит проверку и ловит unique-индекс IX_Users_Email на вставке.
                // Это не 500 — БД корректно отсекла дубль; ведём себя как при уже существующем пользователе.
                // Снимаем неудавшуюся вставку с трекера, иначе следующий SaveChanges (выдача токена письма)
                // повторит insert и снова упрётся в констрейнт.
                db.Entry(user).State = EntityState.Detached;
                var winner = await db.Users.FirstOrDefaultAsync(u => u.Email == em, ct);
                if (winner is null) // конфликт был не по e-mail — не глотаем вслепую, показываем общую ошибку
                    return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=1&mode=register");
                if (winner.EmailConfirmed)
                    return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&error=exists&mode=register");
                await AccountFlow.SendConfirmationEmailAsync(ctx, tokens, email, winner, ret);
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&mode=sent&email={Uri.EscapeDataString(em)}");
            }
            await AccountFlow.SendConfirmationEmailAsync(ctx, tokens, email, user, ret);
            await AccountFlow.SignInCookieAsync(ctx, user);
            await audit.LogAsync(ctx, AuthEventType.Register, em, user.Id);
            return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
        }).RequireRateLimiting("email-send"); // регистрация шлёт письмо → анти-бомбинг

        // ---------------- Подтверждение e-mail по ссылке из письма ----------------
        app.MapGet("/account/confirm", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens,
            AuthAudit audit, string? token, string? @return) =>
        {
            var userId = await tokens.ConsumeAsync(token, EmailTokenPurpose.ConfirmEmail);
            var user = userId is { } id ? await db.Users.FindAsync(id) : null;
            if (user is null) // ссылка недействительна/устарела/использована → на вход с предложением новой ссылки
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(@return ?? "")}&error=badtoken");

            if (!user.EmailConfirmed) { user.EmailConfirmed = true; await db.SaveChangesAsync(); }
            // Подтвердил → сразу вход и возврат туда, откуда пришёл (обычно /connect/authorize → назад в приложение).
            await AccountFlow.SignInCookieAsync(ctx, user);
            await audit.LogAsync(ctx, AuthEventType.EmailConfirmed, user.Email, user.Id);
            return Results.Redirect(string.IsNullOrEmpty(@return) ? "/" : @return);
        }).RequireRateLimiting("auth"); // защита от перебора токена подтверждения

        // ---------------- Переотправка письма подтверждения (нейтральный ответ) ----------------
        app.MapPost("/account/resend", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens, IEmailSender email, AuthAudit audit) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            string em = form["email"].ToString().Trim().ToLowerInvariant();
            string ret = form["return"].ToString();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == em);
            if (user is not null && !user.EmailConfirmed)
            {
                await AccountFlow.SendConfirmationEmailAsync(ctx, tokens, email, user, ret);
                await audit.LogAsync(ctx, AuthEventType.ConfirmationResent, em, user.Id);
            }
            // Нейтрально: всегда «письмо отправлено» (не раскрываем, есть ли такой аккаунт).
            return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}&mode=sent&email={Uri.EscapeDataString(em)}");
        }).RequireRateLimiting("email-send"); // анти-бомбинг переотправкой

        // ---------------- Управление e-mail: смена адреса ДО подтверждения (исправить опечатку) ----------------
        // Требует входа (мягкий гейт → пользователь уже внутри). Подтверждённый адрес здесь не меняем.
        app.MapGet("/account/email", async (HttpContext ctx, AuthDbContext db, string? @return, string? error) =>
        {
            var auth = await ctx.AuthenticateAsync("idp");
            var user = Guid.TryParse(auth.Principal?.FindFirst("sub")?.Value, out var id) ? await db.Users.FindAsync(id) : null;
            if (user is null) return Results.Redirect($"/account/login?return={Uri.EscapeDataString(@return ?? "/")}");
            return Results.Content(AccountPages.AccountEmailPage(user.Email, user.EmailConfirmed, user.PendingEmail, @return ?? "/", error), "text/html; charset=utf-8");
        });

        app.MapPost("/account/change-email", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens, IEmailSender email, AuthAudit audit) =>
        {
            var auth = await ctx.AuthenticateAsync("idp");
            var user = Guid.TryParse(auth.Principal?.FindFirst("sub")?.Value, out var id) ? await db.Users.FindAsync(id) : null;
            var form = await ctx.Request.ReadFormAsync();
            string ret = form["return"].ToString();
            if (user is null) return Results.Redirect($"/account/login?return={Uri.EscapeDataString(ret)}");

            string newEmail = form["email"].ToString().Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(newEmail) || !newEmail.Contains('@'))
                return Results.Redirect($"/account/email?return={Uri.EscapeDataString(ret)}&error=invalid");
            if (await db.Users.AnyAsync(u => u.Email == newEmail && u.Id != user.Id))
                return Results.Redirect($"/account/email?return={Uri.EscapeDataString(ret)}&error=taken");

            if (user.EmailConfirmed)
            {
                // ПОДТВЕРЖДЁННЫЙ адрес: verify-new-before-switch — основной e-mail не трогаем, пока владение новым
                // не доказано переходом по ссылке. Ссылка уходит на НОВЫЙ адрес, уведомление — на СТАРЫЙ (OWASP).
                if (newEmail == user.Email)
                    return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret); // адрес не изменился
                user.PendingEmail = newEmail;
                await db.SaveChangesAsync();

                var raw = await tokens.CreateAsync(user.Id, EmailTokenPurpose.ChangeEmail, EmailTokenService.ConfirmLifetime);
                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                var link = $"{baseUrl}/account/confirm-email-change?token={Uri.EscapeDataString(raw)}&return={Uri.EscapeDataString(ret)}";
                var (subject, html) = EmailTemplates.ConfirmEmailChange(user.DisplayName, link, newEmail, AccountFlow.IsEnCulture());
                await email.SendAsync(newEmail, subject, html);                        // подтверждение — на новый адрес
                var (nSub, nHtml) = EmailTemplates.EmailChangeRequested(user.DisplayName, newEmail, AccountFlow.IsEnCulture());
                await email.SendAsync(user.Email, nSub, nHtml);                        // уведомление — на старый адрес
                await audit.LogAsync(ctx, AuthEventType.EmailChanged, user.Email, user.Id, detail: $"requested:{newEmail}");
                return Results.Redirect($"/account/login?mode=sent&email={Uri.EscapeDataString(newEmail)}&return={Uri.EscapeDataString(ret)}");
            }

            // НЕподтверждённый адрес: исправление опечатки — меняем сразу и шлём подтверждение на новый.
            var oldEmail = user.Email;
            user.Email = newEmail;
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx, AuthEventType.EmailChanged, newEmail, user.Id, detail: $"from:{oldEmail}");
            await AccountFlow.SignInCookieAsync(ctx, user);                                   // обновляем e-mail в cookie
            await AccountFlow.SendConfirmationEmailAsync(ctx, tokens, email, user, ret);      // письмо на новый адрес
            return Results.Redirect($"/account/login?mode=sent&email={Uri.EscapeDataString(newEmail)}&return={Uri.EscapeDataString(ret)}");
        }).RequireRateLimiting("email-send"); // анти-бомбинг сменой адреса

        // ---------------- Смена ПОДТВЕРЖДЁННОГО e-mail: подтверждение нового адреса ----------------
        app.MapGet("/account/confirm-email-change", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens,
            AuthAudit audit, string? token, string? @return) =>
        {
            var userId = await tokens.ConsumeAsync(token, EmailTokenPurpose.ChangeEmail);
            var user = userId is { } id ? await db.Users.FindAsync(id) : null;
            if (user is null || string.IsNullOrEmpty(user.PendingEmail))
                return Results.Redirect($"/account/login?return={Uri.EscapeDataString(@return ?? "")}&error=badtoken");

            var newEmail = user.PendingEmail;
            // Пока ссылка «летела», адрес мог занять кто-то другой — тогда не переключаем.
            if (await db.Users.AnyAsync(u => u.Email == newEmail && u.Id != user.Id))
            {
                user.PendingEmail = null;
                await db.SaveChangesAsync();
                return Results.Redirect($"/account/email?return={Uri.EscapeDataString(@return ?? "/")}&error=taken");
            }

            var oldEmail = user.Email;
            user.Email = newEmail;
            user.PendingEmail = null;
            user.EmailConfirmed = true;
            user.SecurityStamp = Guid.NewGuid().ToString("N"); // смена идентичности → инвалидируем прочие сессии
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx, AuthEventType.EmailChanged, newEmail, user.Id, detail: $"confirmed-from:{oldEmail}");
            await AccountFlow.SignInCookieAsync(ctx, user); // обновляем e-mail и метку в текущей cookie
            return Results.Redirect(string.IsNullOrEmpty(@return) ? "/" : @return);
        }).RequireRateLimiting("auth"); // защита от перебора токена смены адреса

        // ---------------- Сброс пароля: запрос ссылки (нейтральный ответ) ----------------
        app.MapGet("/account/forgot", (string? @return, string? sent, string? email, string? error) =>
            // `sent` строкой: без ?sent (ссылка «Забыли пароль?») bool был ОБЯЗАТЕЛЕН → 400; а "1" не парсится в bool.
            // Параметр не должен ронять страницу — трактуем truthy (POST шлёт sent=true).
            Results.Content(AccountPages.ForgotPasswordPage(@return ?? "/",
                sent is "1" || string.Equals(sent, "true", StringComparison.OrdinalIgnoreCase),
                email, error), "text/html; charset=utf-8"));

        app.MapPost("/account/forgot", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens, IEmailSender email, AuthAudit audit) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            string em = form["email"].ToString().Trim().ToLowerInvariant();
            string ret = form["return"].ToString();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == em);
            if (user is not null)
            {
                var raw = await tokens.CreateAsync(user.Id, EmailTokenPurpose.ResetPassword, EmailTokenService.ResetLifetime);
                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                var link = $"{baseUrl}/account/reset?token={Uri.EscapeDataString(raw)}&return={Uri.EscapeDataString(ret)}";
                var (subject, html) = EmailTemplates.ResetPassword(user.DisplayName, link, AccountFlow.IsEnCulture());
                await email.SendAsync(user.Email, subject, html);
                await audit.LogAsync(ctx, AuthEventType.PasswordResetRequested, em, user.Id);
            }
            // Нейтрально: всегда «письмо отправлено, если такой аккаунт есть» — не раскрываем существование почты.
            return Results.Redirect($"/account/forgot?sent=true&return={Uri.EscapeDataString(ret)}&email={Uri.EscapeDataString(em)}");
        }).RequireRateLimiting("email-send"); // анти-бомбинг письмами сброса

        // ---------------- Сброс пароля: форма нового пароля по ссылке из письма ----------------
        app.MapGet("/account/reset", (string? token, string? @return, string? error) =>
        {
            if (string.IsNullOrWhiteSpace(token)) // без токена форму не показываем
                return Results.Redirect($"/account/forgot?return={Uri.EscapeDataString(@return ?? "/")}");
            return Results.Content(AccountPages.ResetPasswordPage(token, @return ?? "/", error, cfg.MinPasswordLength), "text/html; charset=utf-8");
        });

        app.MapPost("/account/reset", async (HttpContext ctx, AuthDbContext db, EmailTokenService tokens,
            IPasswordHasher<AppUser> hasher, IEmailSender email, IPwnedPasswordChecker pwned,
            IOpenIddictTokenManager tokenManager, IOpenIddictAuthorizationManager authManager, AuthAudit audit, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            string token = form["token"]!;
            string ret = form["return"].ToString();
            string password = form["password"]!;
            string RetToReset(string err) => $"/account/reset?token={Uri.EscapeDataString(token)}&return={Uri.EscapeDataString(ret)}&error={err}";

            // Проверяем пароль ДО погашения токена: при ошибке форму можно повторить по той же ссылке.
            if (!PasswordPolicy.IsAcceptable(password, cfg.MinPasswordLength, out _))
                return Results.Redirect(RetToReset("weak"));

            // Токен одноразовый: гасим и получаем пользователя. Недействителен/просрочен → просим новую ссылку.
            var userId = await tokens.ConsumeAsync(token, EmailTokenPurpose.ResetPassword, ct);
            var user = userId is { } id ? await db.Users.FindAsync([id], ct) : null;
            if (user is null)
                return Results.Redirect($"/account/forgot?return={Uri.EscapeDataString(ret)}&error=badtoken");

            if (cfg.CheckPwned && await pwned.IsPwnedAsync(password, ct))
                return Results.Redirect(RetToReset("pwned"));

            user.PasswordHash = hasher.HashPassword(user, password);
            user.EmailConfirmed = true; // переход по ссылке из письма доказывает владение адресом
            user.SecurityStamp = Guid.NewGuid().ToString("N"); // инвалидирует ВСЕ cookie-сессии на всех устройствах
            await db.SaveChangesAsync(ct);

            // OWASP: смена пароля инвалидирует активные сессии — отзываем все OIDC-токены/разрешения пользователя,
            // чтобы украденные access/refresh-токены умерли. Security-stamp гасит и cookie-сессии IdP немедленно.
            var sub = user.Id.ToString();
            await foreach (var t in tokenManager.FindBySubjectAsync(sub, ct)) await tokenManager.TryRevokeAsync(t, ct);
            await foreach (var a in authManager.FindBySubjectAsync(sub, ct)) await authManager.TryRevokeAsync(a, ct);

            var (subject, html) = EmailTemplates.PasswordChanged(user.DisplayName, AccountFlow.IsEnCulture());
            await email.SendAsync(user.Email, subject, html); // уведомление владельцу о смене пароля

            await AccountFlow.SignInCookieAsync(ctx, user); // новый вход после смены пароля
            await audit.LogAsync(ctx, AuthEventType.PasswordReset, user.Email, user.Id);
            return Results.Redirect(string.IsNullOrEmpty(ret) ? "/" : ret);
        }).RequireRateLimiting("auth"); // защита от перебора reset-токена
    }
}
