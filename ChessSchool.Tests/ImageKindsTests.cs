using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

/// <summary>Валидация типов изображений и ключей объектов S3 (чистые функции, без сети).</summary>
public class ImageKindsTests
{
    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/avif", true)]
    [InlineData("image/gif", true)]
    [InlineData("image/svg+xml", false)] // SVG — вектор со скриптами, не пускаем
    [InlineData("application/pdf", false)]
    [InlineData("text/html", false)]
    [InlineData(null, false)]
    public void IsAllowed_AcceptsOnlyRasterImages(string? contentType, bool expected) =>
        Assert.Equal(expected, ImageKinds.IsAllowed(contentType));

    [Theory]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/png", "png")]
    [InlineData("image/webp", "webp")]
    public void Extension_MapsContentType(string contentType, string ext) =>
        Assert.Equal(ext, ImageKinds.Extension(contentType));

    [Fact]
    public void NewKey_IsValidGuidKeyWithExtension()
    {
        var key = ImageKinds.NewKey("image/webp");
        Assert.EndsWith(".webp", key);
        Assert.True(ImageKinds.IsValidKey(key));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.jpg", true)]
    [InlineData("0123456789abcdef0123456789abcdef.webp", true)]
    [InlineData("../../etc/passwd", false)]          // path traversal
    [InlineData("0123456789abcdef0123456789abcdef.svg", false)] // тип не разрешён
    [InlineData("short.jpg", false)]                 // не 32 hex
    [InlineData("0123456789ABCDEF0123456789ABCDEF.jpg", false)] // верхний регистр не наш формат
    [InlineData("0123456789abcdef0123456789abcdef.jpg/extra", false)]
    public void IsValidKey_GuardsAgainstBadKeys(string key, bool expected) =>
        Assert.Equal(expected, ImageKinds.IsValidKey(key));

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.png", "image/png")]
    [InlineData("0123456789abcdef0123456789abcdef.avif", "image/avif")]
    public void ContentTypeForKey_DerivesFromExtension(string key, string expected) =>
        Assert.Equal(expected, ImageKinds.ContentTypeForKey(key));

    [Fact]
    public async Task NullImageStorage_ReportsUnconfigured_AndOpenReturnsNull()
    {
        IImageStorage storage = new NullImageStorage();
        Assert.False(storage.IsConfigured);
        Assert.Null(await storage.OpenAsync("0123456789abcdef0123456789abcdef.jpg"));
    }
}
