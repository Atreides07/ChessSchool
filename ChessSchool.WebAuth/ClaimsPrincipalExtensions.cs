using System.Security.Claims;

namespace ChessSchool.WebAuth;

/// <summary>
/// Чтение auth-claim'ов из principal — ЕДИНАЯ типизированная точка. Значения claim'ов приходят в том
/// формате, в каком их сериализовал провайдер, поэтому сравнивать их со строковым литералом нельзя.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Подтверждён ли e-mail (мягкий гейт). `email_verified` — стандартный OIDC-claim БУЛЕВ: в userinfo
    /// приходит JSON `true`/`false`, а OIDC-маппинг кладёт его как строку через <c>JsonElement.ToString()</c>,
    /// которая для булева даёт «True»/«False» (с большой буквы!). Поэтому читаем через <c>bool.TryParse</c>
    /// (регистронезависимо), а НЕ через сравнение с литералом "true" — иначе подтверждённый пользователь
    /// считался бы неподтверждённым (баннер + блокировка платных действий). Отсутствие claim ⇒ false.
    /// </summary>
    public static bool IsEmailVerified(this ClaimsPrincipal? user) =>
        bool.TryParse(user?.FindFirst("email_verified")?.Value, out var v) && v;
}
