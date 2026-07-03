using System.Net;

namespace ChessSchool.Auth.Email;

/// <summary>Простые HTML-шаблоны писем IdP (RU/EN), без внешних зависимостей и картинок.</summary>
public static class EmailTemplates
{
    public static (string Subject, string Html) ConfirmEmail(string displayName, string confirmUrl, bool en)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(displayName) ? (en ? "there" : "друг") : displayName);
        var url = WebUtility.HtmlEncode(confirmUrl);
        var subject = en ? "Confirm your email — ChessSchool ID" : "Подтвердите e-mail — ChessSchool ID";
        var greeting = en ? $"Hi {name}," : $"Здравствуйте, {name}!";
        var lead = en
            ? "Confirm your email address to activate your ChessSchool account."
            : "Подтвердите адрес e-mail, чтобы активировать аккаунт ChessSchool.";
        var button = en ? "Confirm email" : "Подтвердить e-mail";
        var fallback = en
            ? "If the button doesn't work, copy this link into your browser:"
            : "Если кнопка не работает, скопируйте ссылку в браузер:";
        var expiry = en
            ? "The link is valid for 24 hours. If you didn't create an account, just ignore this email."
            : "Ссылка действительна 24 часа. Если вы не создавали аккаунт — просто проигнорируйте письмо.";

        var html = $$"""
<!doctype html><html><body style="margin:0;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:#0e1116">
<div style="max-width:480px;margin:0 auto;padding:32px 16px">
  <div style="background:#fff;border:1px solid #d6dae1;border-radius:14px;padding:28px">
    <div style="font-weight:700;font-size:18px;letter-spacing:-.02em;margin-bottom:16px">♟ ChessSchool ID</div>
    <p style="margin:0 0 8px;font-size:15px">{{greeting}}</p>
    <p style="margin:0 0 20px;color:#5b6470;font-size:14px;line-height:1.55">{{lead}}</p>
    <a href="{{url}}" style="display:inline-block;background:#2b6ef2;color:#fff;text-decoration:none;font-weight:600;font-size:15px;padding:12px 22px;border-radius:8px">{{button}}</a>
    <p style="margin:22px 0 6px;color:#8b93a1;font-size:12px">{{fallback}}</p>
    <p style="margin:0 0 20px;word-break:break-all;font-size:12px"><a href="{{url}}" style="color:#2b6ef2">{{url}}</a></p>
    <p style="margin:0;color:#8b93a1;font-size:12px;line-height:1.5">{{expiry}}</p>
  </div>
</div>
</body></html>
""";
        return (subject, html);
    }

    public static (string Subject, string Html) ResetPassword(string displayName, string resetUrl, bool en)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(displayName) ? (en ? "there" : "друг") : displayName);
        var url = WebUtility.HtmlEncode(resetUrl);
        var subject = en ? "Reset your password — ChessSchool ID" : "Сброс пароля — ChessSchool ID";
        var greeting = en ? $"Hi {name}," : $"Здравствуйте, {name}!";
        var lead = en
            ? "We received a request to reset your ChessSchool password. Click below to choose a new one."
            : "Мы получили запрос на сброс пароля ChessSchool. Нажмите кнопку, чтобы задать новый.";
        var button = en ? "Reset password" : "Сбросить пароль";
        var fallback = en
            ? "If the button doesn't work, copy this link into your browser:"
            : "Если кнопка не работает, скопируйте ссылку в браузер:";
        var expiry = en
            ? "The link is valid for 1 hour and can be used once. If you didn't request this, ignore this email — your password stays unchanged."
            : "Ссылка действительна 1 час и одноразова. Если вы не запрашивали сброс — просто проигнорируйте письмо, пароль останется прежним.";

        return (subject, Card(greeting, lead, url, button, fallback, url, expiry));
    }

    /// <summary>Уведомление о состоявшейся смене пароля (OWASP: сообщать владельцу о смене учётных данных).</summary>
    public static (string Subject, string Html) PasswordChanged(string displayName, bool en)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(displayName) ? (en ? "there" : "друг") : displayName);
        var subject = en ? "Your password was changed — ChessSchool ID" : "Пароль изменён — ChessSchool ID";
        var greeting = en ? $"Hi {name}," : $"Здравствуйте, {name}!";
        var lead = en
            ? "Your ChessSchool password was just changed. If this was you, no action is needed."
            : "Пароль вашего аккаунта ChessSchool только что изменён. Если это были вы — делать ничего не нужно.";
        var warn = en
            ? "If you did NOT change it, reset your password immediately and contact support — someone may have access to your account."
            : "Если это были не вы — немедленно сбросьте пароль и напишите в поддержку: возможно, кто-то получил доступ к аккаунту.";

        var html = $$"""
<!doctype html><html><body style="margin:0;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:#0e1116">
<div style="max-width:480px;margin:0 auto;padding:32px 16px">
  <div style="background:#fff;border:1px solid #d6dae1;border-radius:14px;padding:28px">
    <div style="font-weight:700;font-size:18px;letter-spacing:-.02em;margin-bottom:16px">♟ ChessSchool ID</div>
    <p style="margin:0 0 8px;font-size:15px">{{greeting}}</p>
    <p style="margin:0 0 16px;color:#5b6470;font-size:14px;line-height:1.55">{{lead}}</p>
    <p style="margin:0;color:#e5484d;font-size:13px;line-height:1.55">{{warn}}</p>
  </div>
</div>
</body></html>
""";
        return (subject, html);
    }

    /// <summary>Письмо на НОВЫЙ адрес: подтвердить владение, чтобы завершить смену e-mail (verify-new-before-switch).</summary>
    public static (string Subject, string Html) ConfirmEmailChange(string displayName, string confirmUrl, string newEmail, bool en)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(displayName) ? (en ? "there" : "друг") : displayName);
        var addr = WebUtility.HtmlEncode(newEmail);
        var url = WebUtility.HtmlEncode(confirmUrl);
        var subject = en ? "Confirm your new email — ChessSchool ID" : "Подтвердите новый e-mail — ChessSchool ID";
        var greeting = en ? $"Hi {name}," : $"Здравствуйте, {name}!";
        var lead = en
            ? $"Confirm that <b>{addr}</b> is your new email for ChessSchool. Your address won't change until you do."
            : $"Подтвердите, что <b>{addr}</b> — ваш новый e-mail для ChessSchool. Пока адрес не изменится.";
        var button = en ? "Confirm new email" : "Подтвердить новый e-mail";
        var fallback = en
            ? "If the button doesn't work, copy this link into your browser:"
            : "Если кнопка не работает, скопируйте ссылку в браузер:";
        var expiry = en
            ? "The link is valid for 24 hours and can be used once. Didn't request this? Ignore this email."
            : "Ссылка действительна 24 часа и одноразова. Не запрашивали смену? Просто проигнорируйте письмо.";
        return (subject, Card(greeting, lead, url, button, fallback, url, expiry));
    }

    /// <summary>Уведомление на СТАРЫЙ адрес: запрошена смена e-mail (OWASP: сообщать владельцу об изменении идентичности).</summary>
    public static (string Subject, string Html) EmailChangeRequested(string displayName, string newEmail, bool en)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(displayName) ? (en ? "there" : "друг") : displayName);
        var addr = WebUtility.HtmlEncode(newEmail);
        var subject = en ? "Email change requested — ChessSchool ID" : "Запрошена смена e-mail — ChessSchool ID";
        var greeting = en ? $"Hi {name}," : $"Здравствуйте, {name}!";
        var lead = en
            ? $"Someone requested to change your ChessSchool email to <b>{addr}</b>. The change takes effect only after the new address is confirmed."
            : $"Запрошена смена e-mail вашего аккаунта ChessSchool на <b>{addr}</b>. Смена вступит в силу только после подтверждения нового адреса.";
        var warn = en
            ? "If this wasn't you, change your password immediately — someone may have access to your account."
            : "Если это были не вы — немедленно смените пароль: возможно, кто-то получил доступ к аккаунту.";

        var html = $$"""
<!doctype html><html><body style="margin:0;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:#0e1116">
<div style="max-width:480px;margin:0 auto;padding:32px 16px">
  <div style="background:#fff;border:1px solid #d6dae1;border-radius:14px;padding:28px">
    <div style="font-weight:700;font-size:18px;letter-spacing:-.02em;margin-bottom:16px">♟ ChessSchool ID</div>
    <p style="margin:0 0 8px;font-size:15px">{{greeting}}</p>
    <p style="margin:0 0 16px;color:#5b6470;font-size:14px;line-height:1.55">{{lead}}</p>
    <p style="margin:0;color:#e5484d;font-size:13px;line-height:1.55">{{warn}}</p>
  </div>
</div>
</body></html>
""";
        return (subject, html);
    }

    // Общий каркас письма с кнопкой-ссылкой (подтверждение/сброс).
    private static string Card(string greeting, string lead, string url, string button, string fallback, string linkText, string expiry) => $$"""
<!doctype html><html><body style="margin:0;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;color:#0e1116">
<div style="max-width:480px;margin:0 auto;padding:32px 16px">
  <div style="background:#fff;border:1px solid #d6dae1;border-radius:14px;padding:28px">
    <div style="font-weight:700;font-size:18px;letter-spacing:-.02em;margin-bottom:16px">♟ ChessSchool ID</div>
    <p style="margin:0 0 8px;font-size:15px">{{greeting}}</p>
    <p style="margin:0 0 20px;color:#5b6470;font-size:14px;line-height:1.55">{{lead}}</p>
    <a href="{{url}}" style="display:inline-block;background:#2b6ef2;color:#fff;text-decoration:none;font-weight:600;font-size:15px;padding:12px 22px;border-radius:8px">{{button}}</a>
    <p style="margin:22px 0 6px;color:#8b93a1;font-size:12px">{{fallback}}</p>
    <p style="margin:0 0 20px;word-break:break-all;font-size:12px"><a href="{{url}}" style="color:#2b6ef2">{{linkText}}</a></p>
    <p style="margin:0;color:#8b93a1;font-size:12px;line-height:1.5">{{expiry}}</p>
  </div>
</div>
</body></html>
""";
}
