using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ChessSchool.Auth.Email;

/// <summary>Отправитель писем IdP (подтверждение e-mail, сброс пароля).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>Конфиг почты (секция <c>Email</c>). Dev — mailpit (host/port, без auth/TLS); прод — реальный SMTP.</summary>
public sealed class EmailOptions
{
    public string From { get; set; } = "ChessSchool ID <no-reply@chessschool.local>";
    public string? Host { get; set; }
    public int Port { get; set; } = 1025;
    public string? User { get; set; }
    public string? Password { get; set; }
    public bool UseStartTls { get; set; }

    public static EmailOptions FromConfig(IConfiguration config)
    {
        var s = config.GetSection("Email");
        return new EmailOptions
        {
            From = s["From"] ?? "ChessSchool ID <no-reply@chessschool.local>",
            Host = s["Smtp:Host"],
            Port = int.TryParse(s["Smtp:Port"], out var p) ? p : 1025,
            User = s["Smtp:User"],
            Password = s["Smtp:Password"],
            UseStartTls = bool.TryParse(s["Smtp:UseStartTls"], out var t) && t,
        };
    }
}

/// <summary>SMTP-отправитель (MailKit). Локально шлёт в mailpit (без auth/TLS), в проде — на реальный SMTP.</summary>
public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> log) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(options.From));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        var security = options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(options.Host!, options.Port, security, ct); // Host непуст: отправитель создаётся только при заданном хосте
        if (!string.IsNullOrEmpty(options.User))
            await client.AuthenticateAsync(options.User, options.Password ?? "", ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
        log.LogInformation("Письмо отправлено на {To}: {Subject}", to, subject);
    }
}

/// <summary>Фолбэк без SMTP (dev без mailpit / тесты): пишет письмо в лог со ссылкой, чтобы флоу не вставал.
/// Реальную отправку это НЕ выполняет — только для локальной разработки без почтового сервера.</summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> log) : IEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        log.LogWarning("SMTP не настроен — письмо не отправлено, вывожу в лог. To={To} Subject={Subject}\n{Body}",
            to, subject, htmlBody);
        return Task.CompletedTask;
    }
}
