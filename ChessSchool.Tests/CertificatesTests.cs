using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ChessSchool.Auth;
using Microsoft.Extensions.Configuration;

namespace ChessSchool.Tests;

/// <summary>Проверяет загрузку постоянных сертификатов IdP из конфигурации (прод-путь).</summary>
public sealed class CertificatesTests : IDisposable
{
    private readonly string _pfx = Path.Combine(Path.GetTempPath(), $"cert-{Guid.NewGuid():N}.pfx");

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void LoadFromConfig_LoadsPkcs12_WithPrivateKey()
    {
        const string password = "test-pw";
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=chessschool-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using (var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1)))
            File.WriteAllBytes(_pfx, generated.Export(X509ContentType.Pkcs12, password));

        var config = Config(new()
        {
            ["OpenIddict:SigningCertificate:Path"] = _pfx,
            ["OpenIddict:SigningCertificate:Password"] = password,
        });

        using var cert = Certificates.LoadFromConfig(config, "OpenIddict:SigningCertificate");

        Assert.True(cert.HasPrivateKey);
        Assert.Equal("CN=chessschool-test", cert.Subject);
    }

    [Fact]
    public void LoadFromConfig_Throws_WhenPathMissing()
    {
        var config = Config(new());
        Assert.Throws<InvalidOperationException>(
            () => Certificates.LoadFromConfig(config, "OpenIddict:SigningCertificate"));
    }

    public void Dispose()
    {
        if (File.Exists(_pfx)) File.Delete(_pfx);
    }
}
