namespace ChessSchool.ApiService;

/// <summary>
/// Доступ к ЛК школы = BFF: канал закрыт <c>X-Internal-Key</c> (см. <see cref="InternalKeyFilter"/>), а
/// действующего пользователя Web передаёт заголовком <c>X-Acting-Sub</c>. Спуфинг извне невозможен без
/// internal-ключа. Владение школой проверяется в обработчиках через <c>SchoolAccessService</c>.
/// </summary>
public static class AuthzExtensions
{
    public const string ActingSubHeader = "X-Acting-Sub";

    /// <summary>IdP-`sub` действующего пользователя из заголовка, либо null если не задан.</summary>
    public static string? ActingSub(this HttpContext ctx)
    {
        var v = ctx.Request.Headers[ActingSubHeader].ToString();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>Требует непустой <c>X-Acting-Sub</c> на всех эндпоинтах билдера (иначе 401).</summary>
    public static TBuilder RequireActingSub<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.ActingSub() is null ? Results.Unauthorized() : await next(ctx));
        return builder;
    }
}
