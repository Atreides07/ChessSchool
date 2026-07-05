using System.Security.Cryptography;
using System.Text;

namespace ChessSchool.Auth;

/// <summary>
/// TOTP (RFC 6238) для MFA: HMAC-SHA1, шаг 30с, 6 цифр — совместимо с Google Authenticator / 1Password и пр.
/// Плюс Base32 (RFC 4648) для секрета в otpauth-URI. Чистые функции, без внешних зависимостей — юнит-тестируемо
/// по контрольным векторам RFC 6238. Секрет хранится зашифрованным (DataProtection), сюда приходит уже расшифрованным.
/// </summary>
public static class Totp
{
    public const int DefaultDigits = 6;
    public const int DefaultPeriodSeconds = 30;

    /// <summary>Криптостойкий секрет (20 байт = 160 бит, как в RFC), для показа — Base32.</summary>
    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    /// <summary>Код TOTP для заданного счётчика (unixTime/period). Публично для тестов по векторам RFC.</summary>
    public static string ComputeCode(byte[] key, long counter, int digits = DefaultDigits)
    {
        Span<byte> msg = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(msg, counter);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, msg, hash);

        int offset = hash[^1] & 0x0f; // динамическая усечка (RFC 4226 §5.3)
        int binary = ((hash[offset] & 0x7f) << 24)
                   | ((hash[offset + 1] & 0xff) << 16)
                   | ((hash[offset + 2] & 0xff) << 8)
                   | (hash[offset + 3] & 0xff);
        int mod = (int)Math.Pow(10, digits);
        return (binary % mod).ToString().PadLeft(digits, '0');
    }

    /// <summary>Проверка кода с окном ±window шагов (компенсация рассинхрона часов). Constant-time сравнение.</summary>
    public static bool Verify(byte[] key, string? code, DateTimeOffset now, int window = 1,
        int digits = DefaultDigits, int periodSeconds = DefaultPeriodSeconds)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim();
        if (code.Length != digits || !code.All(char.IsDigit)) return false;

        long counter = now.ToUnixTimeSeconds() / periodSeconds;
        for (long w = -window; w <= window; w++)
        {
            var candidate = ComputeCode(key, counter + w, digits);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(code)))
                return true;
        }
        return false;
    }

    /// <summary>otpauth://-URI для QR/ручного ввода в приложении-аутентификаторе.</summary>
    public static string OtpAuthUri(string issuer, string account, byte[] secret)
    {
        // Label = "issuer:account" с ЛИТЕРАЛЬНЫМ двоеточием-разделителем; issuer и account кодируем по
        // отдельности. Форма с закодированным двоеточием (%3A) по спецификации допустима, но часть версий
        // Google Authenticator её не парсит — канонический вид (как otplib/speakeasy) принимается надёжно.
        var iss = Uri.EscapeDataString(issuer);
        var acc = Uri.EscapeDataString(account);
        return $"otpauth://totp/{iss}:{acc}?secret={Base32.Encode(secret)}&issuer={iss}&algorithm=SHA1&digits={DefaultDigits}&period={DefaultPeriodSeconds}";
    }
}

/// <summary>Base32 (RFC 4648) без паддинга — формат секрета для authenticator-приложений.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        if (data.Length == 0) return "";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 0x1f]);
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 0x1f]);
        return sb.ToString();
    }

    public static byte[] Decode(string input)
    {
        input = input.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var bytes = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in input)
        {
            int idx = Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"Недопустимый символ Base32: '{c}'.");
            buffer = (buffer << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xff));
            }
        }
        return bytes.ToArray();
    }
}
