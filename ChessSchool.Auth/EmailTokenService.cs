using System.Security.Cryptography;
using System.Text;
using ChessSchool.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.Auth;

/// <summary>
/// Выпуск и погашение одноразовых e-mail-токенов (подтверждение почты / сброс пароля).
/// Сырой токен уходит только в ссылку письма; в БД лежит его SHA-256-хэш. Погашение — атомарно
/// одноразовое: токен помечается Used, повторный переход по той же ссылке не сработает.
/// </summary>
public sealed class EmailTokenService(AuthDbContext db)
{
    public static readonly TimeSpan ConfirmLifetime = TimeSpan.FromHours(24);

    /// <summary>Создаёт токен для пользователя, гасит прежние неиспользованные того же назначения,
    /// возвращает СЫРОЙ токен (для ссылки в письме).</summary>
    public async Task<string> CreateAsync(Guid userId, EmailTokenPurpose purpose, TimeSpan lifetime, CancellationToken ct = default)
    {
        // Одна активная ссылка на назначение: прежние неиспользованные — погасить. (Загрузка+пометка,
        // а не ExecuteUpdate — работает и на InMemory-провайдере в тестах; активных токенов единицы.)
        var prior = await db.EmailTokens
            .Where(t => t.UserId == userId && t.Purpose == purpose && !t.Used)
            .ToListAsync(ct);
        foreach (var t in prior) t.Used = true;

        var raw = GenerateToken();
        db.EmailTokens.Add(new EmailToken
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow + lifetime,
        });
        await db.SaveChangesAsync(ct);
        return raw;
    }

    /// <summary>Гасит токен по сырому значению. Возвращает UserId при успехе, иначе null
    /// (не найден / чужое назначение / просрочен / уже использован).</summary>
    public async Task<Guid?> ConsumeAsync(string? rawToken, EmailTokenPurpose purpose, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = Hash(rawToken);
        var token = await db.EmailTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || token.Used || token.Purpose != purpose || token.ExpiresAt <= DateTimeOffset.UtcNow)
            return null;
        token.Used = true;
        await db.SaveChangesAsync(ct);
        return token.UserId;
    }

    // 32 байта энтропии в URL-safe base64 (без padding) — годится в ссылку без экранирования.
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Hash(string raw)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(digest);
    }
}
