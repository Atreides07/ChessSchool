namespace ChessSchool.Auth;

/// <summary>
/// HTML-страницы аккаунта (вход/регистрация, управление e-mail, MFA, сброс пароля) — чистая презентация:
/// каждый метод по входным данным строит готовую разметку на едином каркасе <see cref="AuthShell"/>.
/// Вынесено из Program.cs, чтобы логика/wiring не тонули в ~380 строках HTML. Поведение не меняется.
/// </summary>
public static class AccountPages
{
    // Единый каркас страниц аккаунта (CSS/шапка один раз). bodyInner — готовая разметка карточки.
    private static string AuthShell(string lang, string title, string bodyInner) => $$"""
<!doctype html><html lang="{{lang}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{title}} — ChessSchool ID</title>
<style>
:root{--ink:#0e1116;--ink2:#5b6470;--muted:#8b93a1;--line:#d6dae1;--accent:#2b6ef2;--accent-h:#1f5ad8;--bg:#f6f7f9;--surface:#fff}
*{box-sizing:border-box}
body{font-family:-apple-system,"Segoe UI",Roboto,Arial,sans-serif;background:var(--bg);color:var(--ink);display:flex;justify-content:center;align-items:center;min-height:100dvh;margin:0;padding:16px}
.card{background:var(--surface);padding:2rem;border-radius:18px;width:340px;max-width:100%;border:1px solid var(--line);box-shadow:0 12px 40px rgba(14,17,22,.10)}
.brand{display:flex;align-items:center;gap:.55rem;font-weight:720;font-size:1.15rem;letter-spacing:-.02em;margin-bottom:.3rem}
.brand .logo{width:30px;height:30px;display:grid;place-items:center;background:var(--ink);border-radius:8px}
.sub{color:var(--muted);font-size:.85rem;margin:0 0 1.25rem}
h1{font-size:1.25rem;margin:0 0 1rem}
label{font-size:.8rem;color:var(--ink2);font-weight:600}
input{width:100%;padding:.6rem .7rem;margin:.25rem 0 .7rem;border-radius:8px;border:1px solid var(--line);background:var(--surface);color:var(--ink);font-size:.92rem}
input:focus{outline:0;border-color:var(--accent);box-shadow:0 0 0 3px #eaf1fe}
button{width:100%;padding:.65rem;border:0;border-radius:8px;background:var(--accent);color:#fff;font-weight:600;font-size:.95rem;cursor:pointer;margin-top:.3rem}
button:hover{background:var(--accent-h)}
.err{color:#e5484d;font-size:.85rem;background:#fdecec;padding:.5rem .7rem;border-radius:8px;margin:0 0 1rem}
.info{color:#0e6b52;font-size:.88rem;background:#e7f6ef;padding:.6rem .7rem;border-radius:8px;margin:0 0 1rem;line-height:1.5}
.switch{color:var(--ink2);font-size:.85rem;text-align:center;margin:1.1rem 0 0}
.switch a{color:var(--accent);font-weight:600;text-decoration:none}
.switch a:hover{text-decoration:underline}
.muted{color:var(--muted);font-size:.78rem;text-align:center;margin:1.1rem 0 0}
.resend{margin:0 0 1rem;padding:.6rem .7rem;background:#f6f7f9;border:1px solid var(--line);border-radius:8px}
.resend button{margin-top:.4rem}
#mode{display:none}
.view-reg{display:none}
#mode:checked ~ .card .view-login{display:none}
#mode:checked ~ .card .view-reg{display:block}
.switch .as-link{background:none;border:0;color:var(--accent);font-weight:600;cursor:pointer;font-size:.85rem;padding:0;width:auto;margin:0}
.switch .as-link:hover{background:none;text-decoration:underline}
</style></head>
<body>
{{bodyInner}}
</body></html>
""";

    private static string BrandHeader(string sub) =>
        $"""<div class="brand"><span class="logo"><svg viewBox="0 0 45 45" width="18" height="18" fill="#fff"><path d="M18 10c1-1 3-2 5-2 7 0 12 6 12 16v14H13c0-6 3-9 7-12-2 1-5 2-7 1-2-1-2-3-1-5-2 1-4 1-5-1-1-3 1-5 4-7 .5-1 1-2 0-3 1-1 2-1 3 0z"/></svg></span> ChessSchool ID</div><p class="sub">{sub}</p>""";

    public static string LoginPage(string ret, string? error, bool register, bool sent, string? email, int minPw)
    {
        var en = AccountFlow.IsEnCulture();
        string lang = en ? "en" : "ru";
        string retEnc = System.Net.WebUtility.HtmlEncode(ret);
        string retQ = Uri.EscapeDataString(ret);
        string emailEnc = System.Net.WebUtility.HtmlEncode(email ?? "");
        string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
        string secured = en ? "Secured by OpenID Connect" : "Защищено OpenID Connect";
        string resendBtn = en ? "Resend confirmation email" : "Отправить письмо ещё раз";

        // Состояние «письмо отправлено» — отдельная карточка (без переключателя вход/регистрация).
        if (sent)
        {
            string sTitle = en ? "Check your email" : "Проверьте почту";
            string sBody = en
                ? $"We sent a confirmation link to <b>{emailEnc}</b>. Open it to activate your account and finish signing in."
                : $"Мы отправили ссылку для подтверждения на <b>{emailEnc}</b>. Перейдите по ней, чтобы активировать аккаунт и войти.";
            string back = en ? "Back to sign in" : "Вернуться ко входу";
            string sentInner = $"""
<div class="card">
{BrandHeader(sub)}
<h1>{sTitle}</h1>
<p class="info">{sBody}</p>
<form method="post" action="/account/resend">
<input type="hidden" name="return" value="{retEnc}">
<input type="hidden" name="email" value="{emailEnc}">
<button type="submit">{resendBtn}</button></form>
<p class="switch"><a href="/account/login?return={retQ}">{back}</a></p>
<p class="muted">{secured}</p></div>
""";
            return AuthShell(lang, sTitle, sentInner);
        }

        string titleReg = en ? "Sign up" : "Регистрация", titleLogin = en ? "Sign in" : "Вход";
        string lPassword = en ? "Password" : "Пароль", lName = en ? "Name" : "Имя";
        string phName = en ? "Your name" : "Ваше имя", phPass6 = en ? $"At least {minPw} characters" : $"Минимум {minPw} символов";
        string btnLogin = en ? "Sign in" : "Войти", btnCreate = en ? "Create account" : "Создать аккаунт";
        string noAcc = en ? "No account?" : "Нет аккаунта?", doReg = en ? "Sign up" : "Зарегистрироваться";
        string haveAcc = en ? "Already have an account?" : "Уже есть аккаунт?";
        string forgot = en ? "Forgot password?" : "Забыли пароль?";

        string errText = error switch
        {
            "unconfirmed" => en ? "Please confirm your email first — we can resend the link." : "Сначала подтвердите e-mail — можем отправить ссылку ещё раз.",
            "badtoken" => en ? "The confirmation link is invalid or has expired. Request a new one:" : "Ссылка подтверждения недействительна или устарела. Запросите новую:",
            "exists" => en ? "This email is already registered. Sign in instead." : "Этот e-mail уже зарегистрирован. Войдите.",
            "weak" => en ? $"Password too short — at least {minPw} characters." : $"Пароль слишком короткий — минимум {minPw} символов.",
            "pwned" => en ? "This password appears in known data breaches. Choose another." : "Этот пароль есть в известных утечках — выберите другой.",
            null => "",
            _ => en ? "Invalid credentials or email already taken." : "Неверные данные или email уже занят.",
        };
        string errBlock = error is null ? "" : $"<p class=\"err\">{errText}</p>";
        // Форма повторной отправки письма: при unconfirmed — email известен (скрытое поле), при badtoken — вводится.
        string resendBlock = error switch
        {
            "unconfirmed" => $"""<form class="resend" method="post" action="/account/resend"><input type="hidden" name="return" value="{retEnc}"><input type="hidden" name="email" value="{emailEnc}"><button type="submit">{resendBtn}</button></form>""",
            "badtoken" => $"""<form class="resend" method="post" action="/account/resend"><input type="hidden" name="return" value="{retEnc}"><label>Email</label><input name="email" type="email" value="{emailEnc}" placeholder="you@example.com" required><button type="submit">{resendBtn}</button></form>""",
            _ => "",
        };

        string inner = $$"""
<input type="checkbox" id="mode" {{(register ? "checked" : "")}}>
<div class="card">
{{BrandHeader(sub)}}
{{errBlock}}
{{resendBlock}}
<div class="view-login">
<h1>{{titleLogin}}</h1>
<form method="post" action="/account/login">
<input type="hidden" name="return" value="{{retEnc}}">
<label>Email</label><input name="email" type="email" value="{{emailEnc}}" placeholder="you@example.com" required>
<label>{{lPassword}}</label><input name="password" type="password" placeholder="••••••••" required>
<button type="submit">{{btnLogin}}</button></form>
<p class="switch"><a href="/account/forgot?return={{retQ}}">{{forgot}}</a></p>
<p class="switch">{{noAcc}} <label for="mode" class="as-link">{{doReg}}</label></p>
</div>
<div class="view-reg">
<h1>{{titleReg}}</h1>
<form method="post" action="/account/register">
<input type="hidden" name="return" value="{{retEnc}}">
<label>{{lName}}</label><input name="name" placeholder="{{phName}}">
<label>Email</label><input name="email" type="email" value="{{emailEnc}}" placeholder="you@example.com" required>
<label>{{lPassword}}</label><input name="password" type="password" placeholder="{{phPass6}}" required>
<button type="submit">{{btnCreate}}</button></form>
<p class="switch">{{haveAcc}} <label for="mode" class="as-link">{{btnLogin}}</label></p>
</div>
<p class="muted">{{secured}}</p></div>
""";
        return AuthShell(lang, register ? titleReg : titleLogin, inner);
    }

    // Страница управления e-mail (вход есть): переотправка + смена адреса до подтверждения.
    public static string AccountEmailPage(string email, bool confirmed, string? pendingEmail, string ret, string? error)
    {
        var en = AccountFlow.IsEnCulture();
        string lang = en ? "en" : "ru";
        string retEnc = System.Net.WebUtility.HtmlEncode(ret);
        string emailEnc = System.Net.WebUtility.HtmlEncode(email);
        string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
        string title = en ? "Your email" : "Ваш e-mail";
        string back = en ? "← Back" : "← Назад";
        string mfaLink = $"<p class=\"switch\"><a href=\"/account/mfa?return={Uri.EscapeDataString(ret)}\">{(en ? "Two-factor authentication →" : "Двухфакторная аутентификация →")}</a></p>";
        string resendBtn = en ? "Resend confirmation email" : "Отправить письмо ещё раз";
        string changeLbl = en ? "Wrong address? Change it" : "Не тот адрес? Изменить";
        string changeBtn = en ? "Change and resend" : "Изменить и переслать";

        string errText = error switch
        {
            "taken" => en ? "This email is already in use." : "Этот e-mail уже занят.",
            "invalid" => en ? "Enter a valid email." : "Укажите корректный e-mail.",
            _ => "",
        };
        string errBlockTop = string.IsNullOrEmpty(errText) ? "" : $"<p class=\"err\">{errText}</p>";

        if (confirmed)
        {
            // Подтверждённый адрес: смена по схеме verify-new-before-switch (ссылка на новый адрес; старый не меняется).
            string okMsg = en ? "Your e-mail is confirmed ✓" : "Ваш e-mail подтверждён ✓";
            string changeConfirmedLbl = en ? "Change email" : "Изменить e-mail";
            string changeConfirmedBtn = en ? "Send confirmation to new address" : "Отправить подтверждение на новый адрес";
            string pendingBlock = string.IsNullOrEmpty(pendingEmail) ? "" : (en
                ? $"<p class=\"info\">Pending confirmation at <b>{System.Net.WebUtility.HtmlEncode(pendingEmail)}</b>. The change applies once confirmed.</p>"
                : $"<p class=\"info\">Ожидает подтверждения на <b>{System.Net.WebUtility.HtmlEncode(pendingEmail)}</b>. Смена вступит в силу после подтверждения.</p>");
            return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlockTop}<p class="info">{okMsg} <b>{emailEnc}</b></p>{pendingBlock}
<div class="resend"><label>{changeConfirmedLbl}</label>
<form method="post" action="/account/change-email"><input type="hidden" name="return" value="{retEnc}"><input name="email" type="email" placeholder="new@example.com" required><button type="submit">{changeConfirmedBtn}</button></form></div>
{mfaLink}
<p class="switch"><a href="{retEnc}">{back}</a></p></div>
""");
        }

        string pending = en
            ? $"We sent a confirmation link to <b>{emailEnc}</b>. Not confirmed yet — resend it or fix the address."
            : $"Мы отправили ссылку на <b>{emailEnc}</b>. Пока не подтверждён — переотправьте или исправьте адрес.";
        return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlockTop}
<p class="info">{pending}</p>
<form method="post" action="/account/resend"><input type="hidden" name="return" value="{retEnc}"><input type="hidden" name="email" value="{emailEnc}"><button type="submit">{resendBtn}</button></form>
<div class="resend"><label>{changeLbl}</label>
<form method="post" action="/account/change-email"><input type="hidden" name="return" value="{retEnc}"><input name="email" type="email" value="{emailEnc}" placeholder="you@example.com" required><button type="submit">{changeBtn}</button></form></div>
{mfaLink}
<p class="switch"><a href="{retEnc}">{back}</a></p></div>
""");
    }

    // QR-код otpauth-ссылки как inline-SVG (QRCoder, чистый managed — без System.Drawing/нативных зависимостей,
    // без внешних CDN). Работает и с выключенным JS. ECC M — баланс плотности и устойчивости к сбоям сканирования.
    private static string OtpAuthQrSvg(string otpauthUri)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(otpauthUri, QRCoder.QRCodeGenerator.ECCLevel.M);
        var svg = new QRCoder.SvgQRCode(data).GetGraphic(4, "#111827", "#ffffff", drawQuietZones: true);
        // Вписываем в контейнер: SVG имеет фиксированный px-размер → форсим масштаб по ширине контейнера.
        return svg.Replace("<svg ", "<svg style=\"width:100%;height:auto;display:block\" ");
    }

    // Страница настройки MFA: включение (показ секрета/otpauth + подтверждение кодом) либо статус «включено».
    public static string MfaSettingsPage(bool enabled, string? base32Secret, string? otpauthUri, string ret, string? error, bool mustEnable = false)
    {
        var en = AccountFlow.IsEnCulture();
        string lang = en ? "en" : "ru";
        string retEnc = System.Net.WebUtility.HtmlEncode(ret);
        string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
        string title = en ? "Two-factor authentication" : "Двухфакторная аутентификация";
        string back = en ? "← Back" : "← Назад";
        string errBlock = error switch
        {
            "code" => $"<p class=\"err\">{(en ? "Wrong code — try again." : "Неверный код — попробуйте ещё раз.")}</p>",
            "adminlock" => $"<p class=\"err\">{(en ? "2FA is required for admins and can't be turned off." : "Для админов 2FA обязательна и не отключается.")}</p>",
            _ => "",
        };
        // Баннер обязательности для админов (или форс с логина/authorize).
        string requiredBanner = mustEnable && !enabled
            ? $"<p class=\"err\">{(en ? "Two-factor authentication is required for your account. Set it up to continue." : "Для вашего аккаунта двухфакторная аутентификация обязательна. Настройте её, чтобы продолжить.")}</p>"
            : "";

        if (enabled)
        {
            string onMsg = en ? "Two-factor authentication is ON ✓" : "Двухфакторная аутентификация включена ✓";
            string disableLbl = en ? "Turn it off? Enter a current code to confirm" : "Отключить? Введите текущий код для подтверждения";
            string disableBtn = en ? "Disable 2FA" : "Отключить 2FA";
            // Админам отключать нельзя — прячем форму отключения, показываем пояснение.
            string body = mustEnable
                ? $"<p class=\"info\">{(en ? "2FA is required for your account (admin) and can't be turned off." : "Для вашего аккаунта (админ) 2FA обязательна и не отключается.")}</p>"
                : $"""
<div class="resend"><label>{disableLbl}</label>
<form method="post" action="/account/mfa/disable"><input type="hidden" name="return" value="{retEnc}"><input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required><button type="submit">{disableBtn}</button></form></div>
""";
            return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlock}<p class="info">{onMsg}</p>
{body}
<p class="switch"><a href="{retEnc}">{back}</a></p></div>
""");
        }

        string secretEnc = System.Net.WebUtility.HtmlEncode(base32Secret ?? "");
        string uriEnc = System.Net.WebUtility.HtmlEncode(otpauthUri ?? "");
        string qrSvg = string.IsNullOrEmpty(otpauthUri) ? "" : OtpAuthQrSvg(otpauthUri);
        string step1 = en ? "1. Scan this QR code in your authenticator app (Google Authenticator, 1Password…):"
                          : "1. Отсканируйте QR-код в приложении-аутентификаторе (Google Authenticator, 1Password…):";
        string manualLbl = en ? "Can’t scan? Enter this key manually:"
                              : "Не получается отсканировать? Введите ключ вручную:";
        string step2 = en ? "2. Enter the 6-digit code it shows to turn on 2FA:"
                          : "2. Введите 6-значный код из приложения, чтобы включить 2FA:";
        string enableBtn = en ? "Enable 2FA" : "Включить 2FA";
        string linkLbl = en ? "Open in app" : "Открыть в приложении";
        return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlock}{requiredBanner}
<p class="info">{step1}</p>
<div style="display:flex;justify-content:center;margin:0 0 1rem"><div style="background:#fff;border:1px solid var(--line);border-radius:12px;padding:.6rem;line-height:0;width:200px" aria-label="QR otpauth">{qrSvg}</div></div>
<p class="info" style="margin:0 0 .4rem">{manualLbl}</p>
<p style="font-family:ui-monospace,Menlo,Consolas,monospace;font-size:1rem;letter-spacing:.06em;word-break:break-all;background:#f6f7f9;border:1px solid var(--line);border-radius:8px;padding:.6rem .7rem;margin:0 0 .5rem">{secretEnc}</p>
<p class="muted" style="text-align:left;margin:0 0 1rem"><a href="{uriEnc}" style="color:var(--accent)">{linkLbl}</a></p>
<label>{step2}</label>
<form method="post" action="/account/mfa/enable"><input type="hidden" name="return" value="{retEnc}"><input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required><button type="submit">{enableBtn}</button></form>
<p class="switch"><a href="{retEnc}">{back}</a></p></div>
""");
    }

    // Одноразовый показ резервных кодов после включения MFA — их надо сохранить.
    public static string MfaRecoveryCodesPage(IReadOnlyList<string> codes, string ret)
    {
        var en = AccountFlow.IsEnCulture();
        string lang = en ? "en" : "ru";
        string retEnc = System.Net.WebUtility.HtmlEncode(ret);
        string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
        string title = en ? "Save your recovery codes" : "Сохраните резервные коды";
        string lead = en
            ? "2FA is on. Store these one-time codes somewhere safe — each lets you sign in once if you lose your authenticator. They won't be shown again."
            : "2FA включена. Сохраните эти одноразовые коды в надёжном месте — каждый пускает в аккаунт один раз, если потеряете аутентификатор. Больше они не покажутся.";
        string done = en ? "I saved them — continue" : "Я сохранил — продолжить";
        string list = string.Join("", codes.Select(c => $"<li>{System.Net.WebUtility.HtmlEncode(c)}</li>"));
        return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1><p class="info">{lead}</p>
<ul style="font-family:ui-monospace,Menlo,Consolas,monospace;font-size:.95rem;letter-spacing:.04em;columns:2;gap:1rem;list-style:none;padding:.7rem;margin:0 0 1rem;background:#f6f7f9;border:1px solid var(--line);border-radius:8px">{list}</ul>
<p class="switch"><a href="{retEnc}">{done}</a></p></div>
""");
    }

    // Страница второго фактора при входе.
    public static string MfaVerifyPage(string ret, string? error)
    {
        var en = AccountFlow.IsEnCulture();
        string lang = en ? "en" : "ru";
        string retEnc = System.Net.WebUtility.HtmlEncode(ret);
        string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
        string title = en ? "Two-factor verification" : "Второй фактор";
        string lead = en ? "Enter the 6-digit code from your authenticator app (or a recovery code)."
                         : "Введите 6-значный код из приложения-аутентификатора (или резервный код).";
        string verifyBtn = en ? "Verify" : "Подтвердить";
        string errBlock = error is null ? "" : $"<p class=\"err\">{(en ? "Wrong code — try again." : "Неверный код — попробуйте ещё раз.")}</p>";
        return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlock}<p class="info">{lead}</p>
<form method="post" action="/account/mfa/verify"><input type="hidden" name="return" value="{retEnc}"><input name="code" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" autofocus required><button type="submit">{verifyBtn}</button></form></div>
""");
    }

    // Страница запроса сброса пароля: ввод e-mail + нейтральное состояние «письмо отправлено».
    public static string ForgotPasswordPage(string ret, bool sent, string? email, string? error)
    {
        var en = AccountFlow.IsEnCulture();
        string lang = en ? "en" : "ru";
        string retEnc = System.Net.WebUtility.HtmlEncode(ret);
        string retQ = Uri.EscapeDataString(ret);
        string emailEnc = System.Net.WebUtility.HtmlEncode(email ?? "");
        string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
        string secured = en ? "Secured by OpenID Connect" : "Защищено OpenID Connect";
        string title = en ? "Reset password" : "Сброс пароля";
        string back = en ? "Back to sign in" : "Вернуться ко входу";

        if (sent)
        {
            string sBody = en
                ? "If an account exists for that email, we've sent a link to reset the password. The link is valid for 1 hour."
                : "Если аккаунт с таким e-mail существует, мы отправили ссылку для сброса пароля. Ссылка действительна 1 час.";
            return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1><p class="info">{sBody}</p>
<p class="switch"><a href="/account/login?return={retQ}">{back}</a></p>
<p class="muted">{secured}</p></div>
""");
        }

        string lead = en
            ? "Enter your email and we'll send a link to reset your password."
            : "Введите e-mail — пришлём ссылку для сброса пароля.";
        string btn = en ? "Send reset link" : "Отправить ссылку";
        string errText = error == "badtoken"
            ? (en ? "The reset link is invalid or has expired. Request a new one:" : "Ссылка сброса недействительна или устарела. Запросите новую:")
            : "";
        string errBlock = string.IsNullOrEmpty(errText) ? "" : $"<p class=\"err\">{errText}</p>";
        return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlock}
<p class="sub">{lead}</p>
<form method="post" action="/account/forgot">
<input type="hidden" name="return" value="{retEnc}">
<label>Email</label><input name="email" type="email" value="{emailEnc}" placeholder="you@example.com" required>
<button type="submit">{btn}</button></form>
<p class="switch"><a href="/account/login?return={retQ}">{back}</a></p>
<p class="muted">{secured}</p></div>
""");
    }

    // Страница ввода нового пароля по ссылке из письма (token в скрытом поле).
    public static string ResetPasswordPage(string token, string ret, string? error, int minPw)
    {
        var en = AccountFlow.IsEnCulture();
        string lang = en ? "en" : "ru";
        string retEnc = System.Net.WebUtility.HtmlEncode(ret);
        string tokenEnc = System.Net.WebUtility.HtmlEncode(token);
        string sub = en ? "One account for ChessSchool and Arena" : "Единый аккаунт для ChessSchool и Arena";
        string secured = en ? "Secured by OpenID Connect" : "Защищено OpenID Connect";
        string title = en ? "New password" : "Новый пароль";
        string lPassword = en ? "New password" : "Новый пароль";
        string ph = en ? $"At least {minPw} characters" : $"Минимум {minPw} символов";
        string btn = en ? "Save new password" : "Сохранить пароль";

        string errText = error switch
        {
            "weak" => en ? $"Password too short — at least {minPw} characters." : $"Пароль слишком короткий — минимум {minPw} символов.",
            "pwned" => en ? "This password appears in known data breaches. Choose another." : "Этот пароль есть в известных утечках — выберите другой.",
            _ => "",
        };
        string errBlock = string.IsNullOrEmpty(errText) ? "" : $"<p class=\"err\">{errText}</p>";
        return AuthShell(lang, title, $"""
<div class="card">{BrandHeader(sub)}<h1>{title}</h1>{errBlock}
<form method="post" action="/account/reset">
<input type="hidden" name="token" value="{tokenEnc}">
<input type="hidden" name="return" value="{retEnc}">
<label>{lPassword}</label><input name="password" type="password" placeholder="{ph}" required>
<button type="submit">{btn}</button></form>
<p class="muted">{secured}</p></div>
""");
    }
}
