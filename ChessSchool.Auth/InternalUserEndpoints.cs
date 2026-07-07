using ChessSchool.Auth.Data;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.Auth;

/// <summary>Запрос на резолв пользователя по e-mail (server-to-server из ApiService при привязке ученика).</summary>
public record ByEmailRequest(string Email);

/// <summary>
/// Внутренние (server-to-server) эндпоинты резолва пользователей: e-mail → sub (привязка ученика) и
/// батч sub → профиль (человекочитаемый список в админке). Гейт — общий <c>X-Internal-Key</c>.
/// Логика прежняя — вынесена из Program.cs в группу.
/// </summary>
public static class InternalUserEndpoints
{
    public static void MapInternalUserEndpoints(this WebApplication app, string internalKey)
    {
        // ---------------- Внутренний резолв email → sub (привязка ученика в ApiService) ----------------
        app.MapPost("/internal/users/by-email", async (ByEmailRequest req, HttpRequest http, AuthDbContext db,
            CancellationToken ct) =>
        {
            if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();

            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            return user is null
                ? Results.NotFound()
                : Results.Ok(new { sub = user.Id.ToString(), displayName = user.DisplayName });
        });

        // ---------------- Батч-резолв sub → профиль (человекочитаемый список подписок в админке) ----------------
        // Возвращаем только найденных; неизвестные/невалидные sub просто отсутствуют в ответе (вызывающий мержит).
        app.MapPost("/internal/users/by-subs", async (BySubsRequest req, HttpRequest http, AuthDbContext db,
            CancellationToken ct) =>
        {
            if (http.Headers["X-Internal-Key"] != internalKey) return Results.Unauthorized();

            var ids = (req.Subs ?? [])
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue).Select(g => g!.Value).Distinct().ToList();
            if (ids.Count == 0) return Results.Ok(Array.Empty<UserInfo>());

            var users = await db.Users.AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .Select(u => new UserInfo(u.Id.ToString(), u.Email, u.DisplayName))
                .ToListAsync(ct);
            return Results.Ok(users);
        });
    }
}
