using ChessSchool.Auth;
using ChessSchool.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.Tests;

/// <summary>
/// Одноразовые e-mail-токены: погашение работает один раз, чужой/просроченный/использованный не проходят,
/// новый токен гасит прежний того же назначения. На EF InMemory (быстро, без Docker).
/// </summary>
public class EmailTokenServiceTests
{
    private static AuthDbContext NewDb() => new(new DbContextOptionsBuilder<AuthDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Consume_Succeeds_Once_ThenFails()
    {
        var db = NewDb();
        var svc = new EmailTokenService(db);
        var uid = Guid.NewGuid();

        var raw = await svc.CreateAsync(uid, EmailTokenPurpose.ConfirmEmail, TimeSpan.FromHours(1));

        Assert.Equal(uid, await svc.ConsumeAsync(raw, EmailTokenPurpose.ConfirmEmail)); // первый раз — успех
        Assert.Null(await svc.ConsumeAsync(raw, EmailTokenPurpose.ConfirmEmail));       // повторно — нельзя
    }

    [Fact]
    public async Task Consume_Fails_ForWrongToken_WrongPurpose_AndExpired()
    {
        var db = NewDb();
        var svc = new EmailTokenService(db);

        Assert.Null(await svc.ConsumeAsync("bogus-token", EmailTokenPurpose.ConfirmEmail));

        var wrongPurpose = await svc.CreateAsync(Guid.NewGuid(), EmailTokenPurpose.ConfirmEmail, TimeSpan.FromHours(1));
        Assert.Null(await svc.ConsumeAsync(wrongPurpose, EmailTokenPurpose.ResetPassword));

        var expired = await svc.CreateAsync(Guid.NewGuid(), EmailTokenPurpose.ConfirmEmail, TimeSpan.FromSeconds(-1));
        Assert.Null(await svc.ConsumeAsync(expired, EmailTokenPurpose.ConfirmEmail));
    }

    [Fact]
    public async Task Create_Invalidates_PriorUnusedToken_OfSamePurpose()
    {
        var db = NewDb();
        var svc = new EmailTokenService(db);
        var uid = Guid.NewGuid();

        var first = await svc.CreateAsync(uid, EmailTokenPurpose.ConfirmEmail, TimeSpan.FromHours(1));
        var second = await svc.CreateAsync(uid, EmailTokenPurpose.ConfirmEmail, TimeSpan.FromHours(1));

        Assert.Null(await svc.ConsumeAsync(first, EmailTokenPurpose.ConfirmEmail));   // прежний погашен
        Assert.Equal(uid, await svc.ConsumeAsync(second, EmailTokenPurpose.ConfirmEmail));
    }
}
