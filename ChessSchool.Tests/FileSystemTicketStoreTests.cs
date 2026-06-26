using System.Security.Claims;
using ChessSchool.WebAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace ChessSchool.Tests;

/// <summary>
/// Проверяет файловое хранилище тикетов: тикет переживает «перезапуск» (новый экземпляр стора
/// поверх той же папки), истёкший тикет не отдаётся, невалидный ключ отклоняется.
/// </summary>
public sealed class FileSystemTicketStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "csts-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _dp = new PassthroughDataProtection();

    private FileSystemTicketStore NewStore() => new(_dir, _dp);

    private static AuthenticationTicket MakeTicket(string name, DateTimeOffset? expires = null)
    {
        var identity = new ClaimsIdentity([new Claim("name", name)], "test");
        var props = new AuthenticationProperties();
        if (expires is { } e) props.ExpiresUtc = e;
        return new AuthenticationTicket(new ClaimsPrincipal(identity), props,
            CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task Ticket_Survives_NewStoreInstance_SimulatingRestart()
    {
        var key = await NewStore().StoreAsync(MakeTicket("Alice"));

        // «Рестарт сервиса»: тикет в памяти бы пропал, но на диске остаётся.
        var ticket = await NewStore().RetrieveAsync(key);

        Assert.NotNull(ticket);
        Assert.Equal("Alice", ticket!.Principal.FindFirst("name")?.Value);
    }

    [Fact]
    public async Task Expired_Ticket_Returns_Null()
    {
        var store = NewStore();
        var key = await store.StoreAsync(MakeTicket("Bob", DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task Removed_Ticket_Returns_Null()
    {
        var store = NewStore();
        var key = await store.StoreAsync(MakeTicket("Carol"));

        await store.RemoveAsync(key);

        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task Invalid_Key_Is_Rejected()
    {
        var store = NewStore();
        Assert.Null(await store.RetrieveAsync("../../etc/passwd"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Прозрачный «протектор»: тестируем контракт стора (файл/сериализация/срок), а не крипто.
    private sealed class PassthroughDataProtection : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }
}
