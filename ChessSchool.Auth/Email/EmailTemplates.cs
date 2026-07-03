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
}
