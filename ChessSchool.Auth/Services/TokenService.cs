using System.Security.Claims;
using System.Security.Cryptography;
using ChessSchool.Auth.Data;
using ChessSchool.Contracts;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ChessSchool.Auth.Services;

/// <summary>Выпускает access (JWT, RS256) и refresh (opaque) токены.</summary>
public sealed class TokenService(SigningKeyProvider keys, AuthDbContext db, IConfiguration config)
{
    private const int AccessTokenLifetimeSeconds = 900; // 15 минут

    public async Task<TokenResponse> IssueAsync(AppUser user, string issuer, CancellationToken ct)
    {
        var audience = config["Jwt:Audience"] ?? "chessschool-api";
        var now = DateTimeOffset.UtcNow;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("name", user.DisplayName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddSeconds(AccessTokenLifetimeSeconds).UtcDateTime,
            SigningCredentials = keys.SigningCredentials
        };

        var accessToken = new JsonWebTokenHandler().CreateToken(descriptor);

        var refresh = new RefreshToken
        {
            Token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
            UserId = user.Id,
            ExpiresAt = now.AddDays(30)
        };
        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);

        return new TokenResponse(accessToken, "Bearer", AccessTokenLifetimeSeconds, refresh.Token);
    }
}
