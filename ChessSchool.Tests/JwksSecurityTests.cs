using System.Text.Json;
using ChessSchool.Auth.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace ChessSchool.Tests;

/// <summary>
/// Гарантирует, что публичный JWKS НЕ содержит приватных параметров RSA-ключа.
/// Регрессионная защита: утечка приватного ключа = компрометация всей авторизации.
/// </summary>
public class JwksSecurityTests
{
    [Fact]
    public void Jwks_ContainsOnlyPublicKeyMaterial()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"jwks-test-{Guid.NewGuid():N}.pem");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:KeyPath"] = keyPath })
            .Build();

        try
        {
            var provider = new SigningKeyProvider(config, new TestEnv());
            var json = JsonSerializer.Serialize(provider.BuildJwks());

            using var doc = JsonDocument.Parse(json);
            var jwk = doc.RootElement.GetProperty("keys")[0];

            // Публичные параметры присутствуют...
            Assert.Equal("RSA", jwk.GetProperty("kty").GetString());
            Assert.True(jwk.TryGetProperty("n", out _));
            Assert.True(jwk.TryGetProperty("e", out _));

            // ...а приватные — НЕТ.
            foreach (var priv in new[] { "d", "p", "q", "dp", "dq", "qi" })
                Assert.False(jwk.TryGetProperty(priv, out _), $"JWKS не должен содержать приватный параметр '{priv}'");
        }
        finally
        {
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
