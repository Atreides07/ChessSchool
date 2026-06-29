using System.Security.Cryptography;
using System.Text;

namespace ChessSchool.ApiService;

/// <summary>
/// Гейт server-to-server вызовов по заголовку <c>X-Internal-Key</c>. Раньше проверка дублировалась
/// в каждом <c>/internal/*</c>-эндпоинте (≈16 копий одной строки) — единственный источник истины
/// и единая точка изменения теперь здесь. Вешается на группу маршрутов через
/// <see cref="RequireInternalKey{TBuilder}"/>.
/// </summary>
public static class InternalKeyFilter
{
    public const string HeaderName = "X-Internal-Key";

    /// <summary>Требует валидный <c>X-Internal-Key</c> на всех эндпоинтах билдера (группе/маршруте).</summary>
    public static TBuilder RequireInternalKey<TBuilder>(this TBuilder builder, string expectedKey)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedKey);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var provided = ctx.HttpContext.Request.Headers[HeaderName].ToString();
            // Сравнение за постоянное время — не даём таймингом подобрать ключ побайтно.
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), expectedBytes))
                return Results.Unauthorized();
            return await next(ctx);
        });
        return builder;
    }
}
