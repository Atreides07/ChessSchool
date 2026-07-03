using System.Security.Cryptography;
using System.Text;
using ChessSchool.Auth.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.Auth;

/// <summary>
/// MFA (TOTP): хранит секрет ЗАШИФРОВАННЫМ (DataProtection — общий keyring в мультисервере, любая нода
/// расшифрует), проверяет коды через <see cref="Totp"/> и ведёт одноразовые резервные коды (в БД — только
/// SHA-256-хэш). Секрет в открытом виде в БД не лежит; резервные коды показываются пользователю один раз.
/// </summary>
public sealed class MfaService(AuthDbContext db, IDataProtectionProvider dp)
{
    public const string Issuer = "ChessSchool ID";
    private readonly IDataProtector _protector = dp.CreateProtector("ChessSchool.Auth.Mfa.Secret.v1");

    /// <summary>Шифрует секрет (Base32 → protected string) для хранения в БД.</summary>
    public string Protect(byte[] secret) => _protector.Protect(Base32.Encode(secret));

    /// <summary>Расшифровывает секрет из БД в байты ключа TOTP.</summary>
    public byte[] Unprotect(string protectedSecret) => Base32.Decode(_protector.Unprotect(protectedSecret));

    /// <summary>Проверяет TOTP-код против секрета пользователя.</summary>
    public bool VerifyTotp(AppUser user, string? code, DateTimeOffset now) =>
        !string.IsNullOrEmpty(user.MfaSecret) && Totp.Verify(Unprotect(user.MfaSecret), code, now);

    /// <summary>Перегенерирует набор резервных кодов (старые удаляются). Возвращает СЫРЫЕ коды для показа один раз.</summary>
    public async Task<IReadOnlyList<string>> ResetRecoveryCodesAsync(Guid userId, int count = 10, CancellationToken ct = default)
    {
        var old = await db.MfaRecoveryCodes.Where(c => c.UserId == userId).ToListAsync(ct);
        db.MfaRecoveryCodes.RemoveRange(old);

        var codes = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var code = GenerateRecoveryCode();
            codes.Add(code);
            db.MfaRecoveryCodes.Add(new MfaRecoveryCode { UserId = userId, CodeHash = Hash(Normalize(code)) });
        }
        await db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>Гасит резервный код при совпадении (одноразовый). true — код принят.</summary>
    public async Task<bool> ConsumeRecoveryCodeAsync(Guid userId, string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var hash = Hash(Normalize(code));
        var rc = await db.MfaRecoveryCodes.FirstOrDefaultAsync(c => c.UserId == userId && c.CodeHash == hash && !c.Used, ct);
        if (rc is null) return false;
        rc.Used = true;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Удаляет все резервные коды пользователя (при отключении MFA).</summary>
    public async Task ClearRecoveryCodesAsync(Guid userId, CancellationToken ct = default)
    {
        var all = await db.MfaRecoveryCodes.Where(c => c.UserId == userId).ToListAsync(ct);
        db.MfaRecoveryCodes.RemoveRange(all);
        await db.SaveChangesAsync(ct);
    }

    // 8 байт энтропии → 16 hex, сгруппированы для читаемости; перебор гасится rate-limit на verify.
    private static string GenerateRecoveryCode()
    {
        var s = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return $"{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }

    private static string Normalize(string code) => code.Trim().Replace("-", "").Replace(" ", "").ToLowerInvariant();

    private static string Hash(string normalized) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
}
