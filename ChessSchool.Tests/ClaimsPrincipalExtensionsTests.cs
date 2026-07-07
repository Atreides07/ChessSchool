using System.Security.Claims;
using ChessSchool.WebAuth;

namespace ChessSchool.Tests;

/// <summary>
/// IsEmailVerified читает булев OIDC-claim `email_verified` через bool.TryParse (регистронезависимо):
/// провайдер сериализует булев как "True"/"true" — сравнение со строковым литералом дало бы ложный «не подтверждён».
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal With(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    [Theory]
    [InlineData("True", true)]   // как OIDC сериализует булев (JsonElement.ToString())
    [InlineData("true", true)]   // как строку кладёт id-токен
    [InlineData("False", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("yes", false)]   // не-булево → безопасный false
    public void IsEmailVerified_ParsesBooleanCaseInsensitively(string value, bool expected) =>
        Assert.Equal(expected, With(new Claim("email_verified", value)).IsEmailVerified());

    [Fact]
    public void IsEmailVerified_False_WhenClaimAbsent() =>
        Assert.False(With(new Claim("sub", "u1")).IsEmailVerified());

    [Fact]
    public void IsEmailVerified_False_WhenNullPrincipal() =>
        Assert.False(((ClaimsPrincipal?)null).IsEmailVerified());
}
