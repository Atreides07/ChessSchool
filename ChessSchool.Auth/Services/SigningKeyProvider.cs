using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace ChessSchool.Auth.Services;

/// <summary>
/// Держит RSA-ключ подписи токенов. Приватный ключ персистится на диск,
/// поэтому JWKS стабилен между перезапусками — продукты-валидаторы продолжают доверять токенам.
/// В проде ключ берётся из защищённого хранилища (KMS/Key Vault), не из файла.
/// </summary>
public sealed class SigningKeyProvider
{
    private readonly RsaSecurityKey _key;

    public SigningKeyProvider(IConfiguration config, IHostEnvironment env)
    {
        var keyPath = config["Jwt:KeyPath"]
            ?? Path.Combine(env.ContentRootPath, "keys", "signing.pem");

        var rsa = RSA.Create(2048);
        if (File.Exists(keyPath))
        {
            rsa.ImportFromPem(File.ReadAllText(keyPath));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
        }

        // kid — детерминированный отпечаток открытого ключа, чтобы валидаторы могли выбрать ключ из JWKS.
        var publicParams = rsa.ExportParameters(false);
        var kid = Base64UrlEncoder.Encode(SHA256.HashData(publicParams.Modulus!))[..16];
        _key = new RsaSecurityKey(rsa) { KeyId = kid };
    }

    public SigningCredentials SigningCredentials => new(_key, SecurityAlgorithms.RsaSha256);

    public string KeyId => _key.KeyId;

    /// <summary>
    /// Открытый ключ в формате JWKS (RFC 7517) для эндпоинта /.well-known/jwks.json.
    /// ВАЖНО: публикуем ТОЛЬКО публичные параметры (n, e) — приватные (d, p, q, …) не должны утечь.
    /// </summary>
    public object BuildJwks()
    {
        var p = _key.Rsa!.ExportParameters(includePrivateParameters: false);
        return new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = SecurityAlgorithms.RsaSha256,
                    kid = _key.KeyId,
                    n = Base64UrlEncoder.Encode(p.Modulus),
                    e = Base64UrlEncoder.Encode(p.Exponent)
                }
            }
        };
    }
}
