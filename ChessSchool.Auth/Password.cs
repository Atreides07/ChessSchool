using System.Security.Cryptography;
using System.Text;

namespace ChessSchool.Auth;

/// <summary>
/// Политика паролей по NIST 800-63B: длина решает, без обязательной композиции/ротации; проверка утечек —
/// отдельно (см. <see cref="IPwnedPasswordChecker"/>). Только длина (min из конфига, разумный max против DoS).
/// </summary>
public static class PasswordPolicy
{
    public const int MaxLength = 128; // огромные пароли — вектор DoS на хэшировании; NIST рекомендует поддерживать ≥64

    /// <summary>true, если длина в допустимом диапазоне. error: "short"/"long" для сообщения.</summary>
    public static bool IsAcceptable(string? password, int minLength, out string? error)
    {
        if (string.IsNullOrEmpty(password) || password.Length < minLength) { error = "short"; return false; }
        if (password.Length > MaxLength) { error = "long"; return false; }
        error = null;
        return true;
    }
}

/// <summary>Чистые функции проверки утечки пароля через k-anonymity Pwned Passwords (тестируемо без сети).</summary>
public static class PwnedPasswords
{
    /// <summary>SHA-1(password) в верхнем HEX, разбитый на префикс (5) и суффикс (35) для range-API.</summary>
    public static (string Prefix, string Suffix) HashPrefix(string password)
    {
        var hex = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password))); // 40 hex, upper
        return (hex[..5], hex[5..]);
    }

    /// <summary>Есть ли суффикс в ответе range-API с ненулевым счётчиком (padding-строки имеют count=0 — игнор).</summary>
    public static bool RangeContains(string rangeBody, string suffix)
    {
        foreach (var raw in rangeBody.Split('\n'))
        {
            var line = raw.Trim();
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            if (line.AsSpan(0, i).Equals(suffix, StringComparison.OrdinalIgnoreCase))
                return int.TryParse(line.AsSpan(i + 1).Trim(), out var count) && count > 0;
        }
        return false;
    }
}

/// <summary>Проверка пароля по базе утечек HaveIBeenPwned (k-anonymity: наружу уходит только 5-символьный префикс).</summary>
public interface IPwnedPasswordChecker
{
    Task<bool> IsPwnedAsync(string password, CancellationToken ct = default);
}

public sealed class PwnedPasswordChecker(IHttpClientFactory http, ILogger<PwnedPasswordChecker> log) : IPwnedPasswordChecker
{
    public const string HttpClientName = "hibp";

    public async Task<bool> IsPwnedAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var (prefix, suffix) = PwnedPasswords.HashPrefix(password);
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"range/{prefix}");
            req.Headers.Add("Add-Padding", "true"); // паддинг: скрывает реальный размер ответа
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false; // fail-open: недоступность HIBP не блокирует регистрацию
            var body = await resp.Content.ReadAsStringAsync(ct);
            return PwnedPasswords.RangeContains(body, suffix);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "HIBP недоступен — проверку утечки пропускаем (fail-open).");
            return false;
        }
    }
}
