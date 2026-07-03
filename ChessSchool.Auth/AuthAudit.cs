using ChessSchool.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.Auth;

/// <summary>
/// Аудит auth-событий: вход/фейл/регистрация/подтверждение/смена e-mail/сброс пароля пишутся в общий стор
/// (PostgreSQL) для наблюдаемости и детекта аномалий. Секреты (пароли, сырые токены) сюда НЕ попадают.
/// Ошибка записи аудита не должна ронять сам auth-флоу — глушим и логируем (best-effort).
/// </summary>
public sealed class AuthAudit(AuthDbContext db, ILogger<AuthAudit> log)
{
    public async Task LogAsync(HttpContext ctx, AuthEventType type, string? email = null, Guid? userId = null,
        string? detail = null, CancellationToken ct = default)
    {
        try
        {
            db.AuthEvents.Add(new AuthEvent
            {
                Type = type,
                UserId = userId,
                Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(),
                Ip = ctx.Connection.RemoteIpAddress?.ToString(),                       // за прокси корректен (forwarded-заголовки)
                UserAgent = Trim(ctx.Request.Headers.UserAgent.ToString(), 512),
                Detail = Trim(detail, 512),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Аудит — наблюдаемость, а не критичный путь: сбой записи не мешает пользователю войти/сброситься.
            log.LogWarning(ex, "Не удалось записать auth-событие {Type} ({Email}).", type, email);
        }
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? null : (s.Length <= max ? s : s[..max]);
}
