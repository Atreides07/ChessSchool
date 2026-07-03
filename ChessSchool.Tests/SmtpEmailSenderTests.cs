using ChessSchool.Auth.Email;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChessSchool.Tests;

/// <summary>
/// Проверяет РЕАЛЬНЫЙ SMTP-транспорт (MailKit → mailpit): письмо доходит и видно через HTTP API mailpit.
/// Это тот же путь, что локально в Aspire (контейнер mailpit) и в проде (реальный SMTP). Требует Docker.
/// </summary>
public class SmtpEmailSenderTests : IAsyncLifetime
{
    private readonly IContainer _mailpit = new ContainerBuilder()
        .WithImage("axllent/mailpit")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(8025))
        .Build();

    public Task InitializeAsync() => _mailpit.StartAsync();
    public Task DisposeAsync() => _mailpit.DisposeAsync().AsTask();

    [Fact]
    public async Task SendAsync_DeliversMessage_VisibleInMailpit()
    {
        var smtpPort = _mailpit.GetMappedPublicPort(1025);
        var apiPort = _mailpit.GetMappedPublicPort(8025);

        var sender = new SmtpEmailSender(
            new EmailOptions { Host = "localhost", Port = smtpPort, From = "ChessSchool ID <no-reply@test.local>" },
            NullLogger<SmtpEmailSender>.Instance);

        await sender.SendAsync("player@test.local", "Confirm your email — ChessSchool ID",
            "<a href=\"http://localhost/account/confirm?token=abc123\">Confirm</a>");

        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{apiPort}") };
        // Доставка через SMTP синхронна, но дадим mailpit пару попыток на индексацию.
        string body = "";
        for (var i = 0; i < 10; i++)
        {
            body = await http.GetStringAsync("/api/v1/messages");
            if (body.Contains("Confirm your email")) break;
            await Task.Delay(200);
        }

        Assert.Contains("Confirm your email", body);
        Assert.Contains("player@test.local", body);
    }
}
