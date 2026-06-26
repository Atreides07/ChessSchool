using System.Security.Cryptography.X509Certificates;

namespace ChessSchool.Auth;

/// <summary>
/// Загрузка постоянных сертификатов IdP (подпись/шифрование токенов) из конфигурации для прода.
/// Dev-сертификаты OpenIddict эфемерны и разъезжаются между нодами/рестартами, поэтому вне Development
/// нужны фиксированные X.509 (PKCS#12) — их путь/пароль приходят из секретов.
/// </summary>
public static class Certificates
{
    /// <summary>
    /// Грузит PKCS#12-сертификат из файла по ключам конфигурации <c>{section}:Path</c> и
    /// <c>{section}:Password</c>. Бросает, если путь не задан (fail-fast вместо тихого dev-поведения).
    /// </summary>
    public static X509Certificate2 LoadFromConfig(IConfiguration config, string section)
    {
        var path = config[$"{section}:Path"];
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"{section}:Path не задан — для прод-IdP нужен постоянный сертификат (PKCS#12) из секрета.");

        var password = config[$"{section}:Password"];
        // DefaultKeySet — кросс-платформенно (EphemeralKeySet не поддержан на macOS-разработке);
        // на Linux-контейнерах приватный ключ временно кладётся в каталог ключей и подчищается.
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }
}
