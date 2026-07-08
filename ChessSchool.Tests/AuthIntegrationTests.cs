using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChessSchool.Auth;
using ChessSchool.Auth.Data;
using ChessSchool.Auth.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace ChessSchool.Tests;

/// <summary>
/// Интеграционные тесты IdP против реального PostgreSQL (Testcontainers). Покрывают: JWKS без приватного
/// материала; МЯГКИЙ гейт (регистрация сразу логинит и шлёт письмо; логин пускает неподтверждённого);
/// смену e-mail до подтверждения (исправить опечатку); погашение токена; протухшую cookie. Письма
/// перехватывает фейковый <see cref="IEmailSender"/>. Требует Docker.
/// </summary>
[Trait("Category", "Docker")] // Testcontainers PostgreSQL → быстрый цикл: dotnet test --filter "Category!=Docker"
public class AuthIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:18.3").Build();
    private AuthFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _factory = new AuthFactory(_pg.GetConnectionString());
        _ = _factory.Services;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Jwks_ExposesOnlyPublicKeyMaterial()
    {
        var client = _factory.CreateClient();
        var json = await client.GetStringAsync("/.well-known/jwks");
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("keys");

        Assert.True(keys.GetArrayLength() >= 1);
        foreach (var jwk in keys.EnumerateArray())
        {
            if (jwk.GetProperty("kty").GetString() == "RSA")
            {
                Assert.True(jwk.TryGetProperty("n", out _));
                Assert.True(jwk.TryGetProperty("e", out _));
            }
            foreach (var priv in new[] { "d", "p", "q", "dp", "dq", "qi" })
                Assert.False(jwk.TryGetProperty(priv, out _), $"JWKS не должен содержать приватный параметр '{priv}'");
        }
    }

    [Fact]
    public async Task Register_SignsInImmediately_UserUnconfirmed_EmailSent()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"reg-{Guid.NewGuid():N}@test.local";

        var register = await Register(client, email);

        // Мягкий гейт: пускаем сразу (redirect в приложение, ставится cookie idp), но e-mail не подтверждён.
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        Assert.Equal("/", register.Headers.Location!.OriginalString);
        Assert.Contains(register.Headers.GetValues("Set-Cookie"), c => c.StartsWith("idp_sso"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.False((await db.Users.FirstAsync(u => u.Email == email)).EmailConfirmed);
        }

        var msg = Assert.Single(_factory.Sent, m => m.To == email);
        Assert.Contains("token=", msg.Html);
    }

    [Fact]
    public async Task RegisterPage_RendersPasswordAndConfirmFields()
    {
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/account/login?mode=register");
        // Форма регистрации отдаёт оба поля пароля (пароль + подтверждение).
        Assert.Contains("name=\"password\"", html);
        Assert.Contains("name=\"password2\"", html);
    }

    [Fact]
    public async Task Register_ConcurrentSameEmail_NoServerError_CreatesExactlyOneUser()
    {
        // TOCTOU-гонка: два параллельных запроса регистрации одним e-mail. Проверка existing и вставка
        // не атомарны → второй ловит unique-индекс IX_Users_Email. Раньше это давало 500; теперь дубль
        // обрабатывается как «уже существует» (не падаем), и в БД ровно один пользователь.
        var email = $"race-{Guid.NewGuid():N}@test.local";
        var c1 = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var c2 = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var responses = await Task.WhenAll(Register(c1, email), Register(c2, email));

        foreach (var r in responses)
        {
            Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode); // никакого 500
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email)); // ровно один создан
    }

    [Fact]
    public async Task Login_Succeeds_EvenWhenEmailUnconfirmed()
    {
        var reg = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"soft-{Guid.NewGuid():N}@test.local";
        await Register(reg, email);

        // Свежий клиент (без cookie): вход неподтверждённого проходит (мягкий гейт), без ошибки.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Login(client, email);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.DoesNotContain("error", login.Headers.Location!.OriginalString);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("idp_sso"));
    }

    [Fact]
    public async Task ChangeEmail_BeforeConfirmation_UpdatesAddress_ResendsAndInvalidatesOldToken()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var oldEmail = $"old-{Guid.NewGuid():N}@test.local";
        var newEmail = $"new-{Guid.NewGuid():N}@test.local";

        await Register(client, oldEmail);           // вошли (мягкий гейт), письмо на старый адрес
        var oldToken = ConfirmToken(oldEmail);

        // Опечатка → меняем адрес до подтверждения (клиент аутентифицирован cookie от register).
        var change = await client.PostAsync("/account/change-email", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = newEmail,
            ["return"] = "/"
        }));
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True(await db.Users.AnyAsync(u => u.Email == newEmail));
            Assert.False(await db.Users.AnyAsync(u => u.Email == oldEmail));
        }

        // Старая ссылка больше не работает, новая — подтверждает.
        var newToken = ConfirmToken(newEmail);
        Assert.Contains("error=badtoken", (await client.GetAsync($"/account/confirm?token={oldToken}")).Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/account/confirm?token={newToken}")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True((await db.Users.FirstAsync(u => u.Email == newEmail)).EmailConfirmed);
        }
    }

    [Fact]
    public async Task Confirm_WithBadToken_RedirectsToBadToken()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/account/confirm?token=definitely-not-a-valid-token");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=badtoken", response.Headers.Location!.OriginalString);
    }

    // Регрессия: minimal-API bool-биндинг ронял страницы. Редиректы шлют ?required=1 / POST шлёт ?sent=true,
    // но bool-параметр парсит лишь true/false, а не-nullable bool ещё и ОБЯЗАТЕЛЕН → 400 BadHttpRequestException.
    [Fact]
    public async Task Mfa_WithRequiredEqualsOne_DoesNotFailBinding()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/account/mfa?required=1&return=%2F");
        // Аноним → редирект на логин (НЕ 400/500 из-за привязки "1" к bool).
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Forgot_WithoutSentParam_RendersPage_NotBadRequest()
    {
        var client = _factory.CreateClient();
        // Ссылка «Забыли пароль?» ведёт на /account/forgot БЕЗ ?sent — non-nullable bool был обязателен → 400.
        var response = await client.GetAsync("/account/forgot?return=%2F");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Forgot_WithSentEqualsOne_RendersPage()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/account/forgot?sent=1&return=%2F");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MfaSetup_ShowsBothQrCodeAndManualKey()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Register(client, $"mfa-setup-{Guid.NewGuid():N}@test.local"); // авто-логин → есть idp-сессия

        var response = await client.GetAsync("/account/mfa?return=%2F");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // QR-код отрисован inline-SVG (сканирование).
        Assert.Contains("<svg", html);
        // Регрессия: SVG QR обязан нести viewBox. Без него CSS width:100% сужал вьюпорт, а координаты не
        // масштабировались → правый/нижний край QR обрезался («не полностью») и Google Authenticator не сканировал.
        Assert.Contains("viewBox", html);
        // И ключ символами доступен (ручной ввод) — обе опции сразу (метка культуронезависимо RU/EN).
        Assert.True(html.Contains("Введите ключ вручную") || html.Contains("Enter this key manually"),
            "на странице должен быть блок ручного ввода ключа");
        // Ссылка/URI otpauth (открыть в приложении) — часть ручного пути.
        Assert.Contains("otpauth://", html);
        // Плюс форма подтверждения кодом.
        Assert.Contains("/account/mfa/enable", html);
    }

    [Fact]
    public async Task Authorize_WhenCookieUserMissing_RedirectsToLogin_NotServerError()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Регистрация сразу логинит (мягкий гейт) → есть idp-сессия (cookie).
        var email = $"stale-{Guid.NewGuid():N}@test.local";
        await Register(client, email);

        // Удаляем пользователя — cookie указывает на несуществующего.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Users.Remove(await db.Users.FirstAsync(u => u.Email == email));
            await db.SaveChangesAsync();
        }

        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes("test-verifier-0123456789-0123456789-0123456789")));
        var redirectUri = Uri.EscapeDataString("https://localhost:5001/signin-oidc");
        var authorizeUrl =
            $"/connect/authorize?client_id=chessschool-web&redirect_uri={redirectUri}" +
            $"&response_type=code&scope=openid&code_challenge={challenge}&code_challenge_method=S256";

        var response = await client.GetAsync(authorizeUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/account/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Register_RejectsShortPassword_NoUserCreated()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"short-{Guid.NewGuid():N}@test.local";
        var r = await client.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "X",
            ["email"] = email,
            ["password"] = "short", // 5 символов < 8 (NIST-минимум)
            ["return"] = "/"
        }));

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("error=weak", r.Headers.Location!.OriginalString);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Register_RejectsPasswordMismatch_NoUserCreated_NoSession()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"mismatch-{Guid.NewGuid():N}@test.local";
        var r = await client.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "X",
            ["email"] = email,
            ["password"] = "secret123",
            ["password2"] = "secret124", // не совпадает с подтверждением
            ["return"] = "/"
        }));

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("error=mismatch", r.Headers.Location!.OriginalString);
        // Пользователь не создан и сессия не выдана — регистрация отклонена до записи в БД.
        Assert.DoesNotContain(r.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.StartsWith("idp_sso"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.False(await db.Users.AnyAsync(u => u.Email == email));
    }

    [Fact]
    public async Task Forgot_ThenReset_ChangesPassword_ConfirmsEmail_NotifiesOwner()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"reset-{Guid.NewGuid():N}@test.local";
        await Register(client, email); // неподтверждённый, пароль "secret123"

        // Запрос сброса — нейтральный ответ «письмо отправлено».
        var forgot = await Forgot(client, email);
        Assert.Equal(HttpStatusCode.Redirect, forgot.StatusCode);
        Assert.Contains("sent=true", forgot.Headers.Location!.OriginalString);

        // По ссылке из письма задаём новый пароль.
        var token = ResetToken(email);
        const string newPassword = "brand-new-passphrase";
        var reset = await Reset(client, token, newPassword);
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.DoesNotContain("error", reset.Headers.Location!.OriginalString);

        // Сброс доказал владение адресом → e-mail подтверждён.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True((await db.Users.FirstAsync(u => u.Email == email)).EmailConfirmed);
        }

        // Владельцу ушло уведомление о смене пароля (RU/EN — по культуре запроса).
        Assert.Contains(_factory.Sent, m => m.To == email &&
            (m.Subject.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Subject.Contains("Пароль", StringComparison.OrdinalIgnoreCase)));

        // Старый пароль больше не подходит, новый — работает.
        var c2 = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var oldLogin = await LoginWith(c2, email, "secret123");
        Assert.Contains("error", oldLogin.Headers.Location!.OriginalString);

        var c3 = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var newLogin = await LoginWith(c3, email, newPassword);
        Assert.DoesNotContain("error", newLogin.Headers.Location!.OriginalString);
        Assert.Contains(newLogin.Headers.GetValues("Set-Cookie"), c => c.StartsWith("idp_sso"));
    }

    [Fact]
    public async Task Forgot_ForUnknownEmail_IsNeutral_NoEmailSent()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"nobody-{Guid.NewGuid():N}@test.local";

        var forgot = await Forgot(client, email);

        Assert.Equal(HttpStatusCode.Redirect, forgot.StatusCode);
        Assert.Contains("sent=true", forgot.Headers.Location!.OriginalString); // тот же нейтральный ответ
        Assert.DoesNotContain(_factory.Sent, m => m.To == email);              // но письма нет
    }

    [Fact]
    public async Task Reset_WithBadToken_RedirectsToForgot()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var reset = await Reset(client, "definitely-not-a-valid-token", "brand-new-passphrase");
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Contains("/account/forgot", reset.Headers.Location!.OriginalString);
        Assert.Contains("error=badtoken", reset.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Reset_RejectsShortPassword_TokenStaysUsable()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var email = $"resetshort-{Guid.NewGuid():N}@test.local";
        await Register(client, email);
        await Forgot(client, email);
        var token = ResetToken(email);

        // Короткий пароль отклоняется ДО погашения токена (форму можно повторить).
        var weak = await Reset(client, token, "short");
        Assert.Contains("error=weak", weak.Headers.Location!.OriginalString);

        // Тот же токен всё ещё действует — задаём нормальный пароль.
        var ok = await Reset(client, token, "brand-new-passphrase");
        Assert.DoesNotContain("error", ok.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task AuthEvents_AreAudited_ForRegisterLoginFailureAndSuccess()
    {
        var email = $"audit-{Guid.NewGuid():N}@test.local";
        await Register(_factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), email);
        await LoginWith(_factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), email, "wrong-password");
        await LoginWith(_factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), email, "secret123");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var events = await db.AuthEvents.Where(e => e.Email == email).ToListAsync();

        Assert.Contains(events, e => e.Type == AuthEventType.Register);
        Assert.Contains(events, e => e.Type == AuthEventType.LoginFailure);
        Assert.Contains(events, e => e.Type == AuthEventType.LoginSuccess);
        Assert.All(events, e => Assert.NotEqual(default, e.CreatedAt)); // время события зафиксировано
        // Секреты в аудит не попадают: ни пароль, ни его хэш не должны оказаться в деталях события.
        Assert.DoesNotContain(events, e => e.Detail != null && (e.Detail.Contains("secret123") || e.Detail.Contains("wrong-password")));
    }

    [Fact]
    public async Task PasswordReset_InvalidatesCookieSessionOnOtherDevice()
    {
        var email = $"invalidate-{Guid.NewGuid():N}@test.local";

        // Устройство A: вошли (мягкий гейт), cookie хранится в клиенте.
        var deviceA = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Register(deviceA, email);

        var authorizeUrl = AuthorizeUrl();
        // База: сессия A валидна — authorize НЕ уводит на логин.
        var before = await deviceA.GetAsync(authorizeUrl);
        Assert.DoesNotContain("/account/login", before.Headers.Location!.OriginalString);

        // Устройство B: сброс пароля перевыпускает security-stamp.
        var deviceB = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Forgot(deviceB, email);
        await Reset(deviceB, ResetToken(email), "brand-new-passphrase");

        // Старая cookie устройства A теперь отклоняется (метка устарела) — authorize ведёт на логин.
        var after = await deviceA.GetAsync(authorizeUrl);
        Assert.Contains("/account/login", after.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ChangeEmail_AfterConfirmation_VerifyNewBeforeSwitch()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var oldEmail = $"conf-old-{Guid.NewGuid():N}@test.local";
        var newEmail = $"conf-new-{Guid.NewGuid():N}@test.local";

        // Регистрируемся и ПОДТВЕРЖДАЕМ старый адрес.
        await Register(client, oldEmail);
        await client.GetAsync($"/account/confirm?token={ConfirmToken(oldEmail)}");

        // Запрашиваем смену на новый адрес (подтверждённый e-mail → verify-new-before-switch).
        var change = await client.PostAsync("/account/change-email", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = newEmail,
            ["return"] = "/"
        }));
        Assert.Equal(HttpStatusCode.Redirect, change.StatusCode);

        // Пока новый адрес НЕ подтверждён: основной e-mail не изменился, новый висит в PendingEmail.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True(await db.Users.AnyAsync(u => u.Email == oldEmail));
            Assert.False(await db.Users.AnyAsync(u => u.Email == newEmail));
            Assert.Equal(newEmail, (await db.Users.FirstAsync(u => u.Email == oldEmail)).PendingEmail);
        }

        // Ссылка подтверждения ушла на НОВЫЙ адрес; уведомление — на СТАРЫЙ.
        Assert.Contains(_factory.Sent, m => m.To == newEmail && m.Html.Contains("/account/confirm-email-change?token="));
        Assert.Contains(_factory.Sent, m => m.To == oldEmail &&
            (m.Subject.Contains("change", StringComparison.OrdinalIgnoreCase) || m.Subject.Contains("смена", StringComparison.OrdinalIgnoreCase)));

        // Переходим по ссылке из письма на новый адрес → адрес переключается.
        var changeToken = ChangeEmailToken(newEmail);
        var confirmChange = await client.GetAsync($"/account/confirm-email-change?token={changeToken}");
        Assert.Equal(HttpStatusCode.Redirect, confirmChange.StatusCode);
        Assert.DoesNotContain("error", confirmChange.Headers.Location!.OriginalString);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True(await db.Users.AnyAsync(u => u.Email == newEmail));
            Assert.False(await db.Users.AnyAsync(u => u.Email == oldEmail));
            var user = await db.Users.FirstAsync(u => u.Email == newEmail);
            Assert.Null(user.PendingEmail);
            Assert.True(user.EmailConfirmed);
        }

        // Одноразовость: повторный переход по той же ссылке не срабатывает.
        var replay = await client.GetAsync($"/account/confirm-email-change?token={changeToken}");
        Assert.Contains("error=badtoken", replay.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Login_FromNewIp_NotifiesOwner()
    {
        var email = $"newip-{Guid.NewGuid():N}@test.local";
        await Register(_factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), email);

        static bool IsNewSignIn((string To, string Subject, string Html) m) =>
            m.Subject.Contains("sign-in", StringComparison.OrdinalIgnoreCase) || m.Subject.Contains("Новый вход", StringComparison.OrdinalIgnoreCase);

        // Вход №1 с IP A: уведомления нет (первый успешный вход — «прежних» нет).
        await LoginFromIp(email, "1.1.1.1");
        Assert.DoesNotContain(_factory.Sent, m => m.To == email && IsNewSignIn(m));

        // Вход №2 с того же IP: тоже нет (устройство уже известно).
        await LoginFromIp(email, "1.1.1.1");
        Assert.DoesNotContain(_factory.Sent, m => m.To == email && IsNewSignIn(m));

        // Вход №3 с НОВОГО IP: владельцу уходит уведомление + фиксируется событие NewDeviceLogin.
        await LoginFromIp(email, "2.2.2.2");
        Assert.Contains(_factory.Sent, m => m.To == email && IsNewSignIn(m));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.True(await db.AuthEvents.AnyAsync(e => e.Email == email && e.Type == AuthEventType.NewDeviceLogin));
    }

    [Fact]
    public async Task Mfa_EnableThenLogin_RequiresSecondFactor_AndRecoveryCodeIsOneTime()
    {
        var email = $"mfa-{Guid.NewGuid():N}@test.local";
        var enroll = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Register(enroll, email);

        // Настройка: страница отдаёт секрет; включаем MFA, подтвердив код из «приложения».
        var page = await enroll.GetStringAsync("/account/mfa");
        var secret = Regex.Match(page, @"letter-spacing:\.06em[^>]*>([A-Z2-7]+)<").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(secret));

        var enableResp = await enroll.PostAsync("/account/mfa/enable", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = TotpNow(secret),
            ["return"] = "/"
        }));
        Assert.Equal(HttpStatusCode.OK, enableResp.StatusCode); // страница с резервными кодами
        var recovery = Regex.Matches(await enableResp.Content.ReadAsStringAsync(), @"<li>([0-9a-f-]{19})</li>")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(recovery);

        // Вход: пароль верный, но MFA включена → уводит на второй фактор, полной сессии ещё нет.
        var device = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await Login(device, email);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("/account/mfa/verify", login.Headers.Location!.OriginalString);
        Assert.False(login.Headers.TryGetValues("Set-Cookie", out var c1) && c1.Any(c => c.StartsWith("idp_sso")));

        // Верный TOTP → полный вход (ставится idp_sso).
        var verify = await device.PostAsync("/account/mfa/verify", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = TotpNow(secret),
            ["return"] = "/"
        }));
        Assert.Equal(HttpStatusCode.Redirect, verify.StatusCode);
        Assert.Contains(verify.Headers.GetValues("Set-Cookie"), c => c.StartsWith("idp_sso"));

        // Резервный код — одноразовый: первый раз пускает, повтор того же кода отклоняется.
        var rc = recovery[0];
        var d2 = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Login(d2, email);
        var okRc = await d2.PostAsync("/account/mfa/verify", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = rc, ["return"] = "/" }));
        Assert.Contains(okRc.Headers.GetValues("Set-Cookie"), c => c.StartsWith("idp_sso"));

        var d3 = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Login(d3, email);
        var reuse = await d3.PostAsync("/account/mfa/verify", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = rc, ["return"] = "/" }));
        Assert.Contains("error", reuse.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Admin_WithoutMfa_IsForcedToEnroll_AtLoginAndAuthorize()
    {
        const string adminEmail = "admin-forced@test.local"; // в Admin:Emails фабрики
        // Регистрация создаёт+входит (сам register MFA не форсит). Затем свежий вход и authorize.
        await Register(_factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), adminEmail);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await LoginWith(client, adminEmail, "secret123");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("/account/mfa", login.Headers.Location!.OriginalString);      // форс настройки
        Assert.Contains("required=1", login.Headers.Location!.OriginalString);

        // Жёсткий гейт: authorize админу без MFA код не выдаёт — уводит на настройку, а не в приложение.
        var authz = await client.GetAsync(AuthorizeUrl());
        Assert.Equal(HttpStatusCode.Redirect, authz.StatusCode);
        // Редирект на нашу /account/mfa (относительный), а НЕ на redirect_uri приложения с кодом.
        Assert.StartsWith("/account/mfa", authz.Headers.Location!.OriginalString);
    }

    // ---- helpers ----

    private static string TotpNow(string base32Secret) =>
        Totp.ComputeCode(Base32.Decode(base32Secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / Totp.DefaultPeriodSeconds);

    private async Task<HttpResponseMessage> LoginFromIp(string email, string ip)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip); // forwarded-headers → Connection.RemoteIpAddress
        return await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "secret123",
            ["return"] = "/"
        }));
    }

    private string ChangeEmailToken(string email)
    {
        var html = _factory.Sent.Last(m => m.To == email && m.Html.Contains("/account/confirm-email-change?token=")).Html;
        var m = Regex.Match(html, @"confirm-email-change\?token=([A-Za-z0-9_\-]+)");
        Assert.True(m.Success, "в письме не найден токен смены адреса");
        return m.Groups[1].Value;
    }

    private static string AuthorizeUrl()
    {
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes("test-verifier-0123456789-0123456789-0123456789")));
        var redirectUri = Uri.EscapeDataString("https://localhost:5001/signin-oidc");
        return $"/connect/authorize?client_id=chessschool-web&redirect_uri={redirectUri}" +
               $"&response_type=code&scope=openid&code_challenge={challenge}&code_challenge_method=S256";
    }

    private static Task<HttpResponseMessage> Forgot(HttpClient client, string email) =>
        client.PostAsync("/account/forgot", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["return"] = "/"
        }));

    private static Task<HttpResponseMessage> Reset(HttpClient client, string token, string password) =>
        client.PostAsync("/account/reset", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["password"] = password,
            ["return"] = "/"
        }));

    private static Task<HttpResponseMessage> LoginWith(HttpClient client, string email, string password) =>
        client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["return"] = "/"
        }));

    private string ResetToken(string email)
    {
        var html = _factory.Sent.Last(m => m.To == email && m.Html.Contains("/account/reset?token=")).Html;
        var m = Regex.Match(html, @"reset\?token=([A-Za-z0-9_\-]+)");
        Assert.True(m.Success, "в письме не найден токен сброса");
        return m.Groups[1].Value;
    }


    private static Task<HttpResponseMessage> Register(HttpClient client, string email) =>
        client.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "Test User",
            ["email"] = email,
            ["password"] = "secret123",
            ["password2"] = "secret123", // подтверждение пароля должно совпадать
            ["return"] = "/"
        }));

    private static Task<HttpResponseMessage> Login(HttpClient client, string email) =>
        client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "secret123",
            ["return"] = "/"
        }));

    private string ConfirmToken(string email)
    {
        var html = _factory.Sent.Last(m => m.To == email).Html;
        var m = Regex.Match(html, @"token=([A-Za-z0-9_\-]+)");
        Assert.True(m.Success, "в письме не найден токен подтверждения");
        return m.Groups[1].Value;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class AuthFactory(string connectionString) : WebApplicationFactory<ChessSchool.Auth.AuthMarker>
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:authdb"] = connectionString,
                ["Sso:Clients:chessschool-web"] = "https://localhost:5001",
                // Эти тесты много раз шлют письма/логинятся — поднимаем лимиты, чтобы не упираться в rate-limiter.
                ["RateLimit:Auth:Permit"] = "100000",
                ["RateLimit:Email:Permit"] = "100000",
                ["Auth:Password:CheckPwned"] = "false", // не ходить в HIBP из тестов (и "secret123" числится в утечках)
                ["Auth:SecurityStamp:ValidateMinutes"] = "0", // проверять метку на каждом запросе (для теста инвалидации)
                ["Admin:Emails"] = "admin-forced@test.local" // для теста обязательной MFA у админов
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new CapturingEmailSender(Sent));
            });
    }

    private sealed class CapturingEmailSender(List<(string, string, string)> sink) : IEmailSender
    {
        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        {
            lock (sink) sink.Add((to, subject, htmlBody));
            return Task.CompletedTask;
        }
    }
}
