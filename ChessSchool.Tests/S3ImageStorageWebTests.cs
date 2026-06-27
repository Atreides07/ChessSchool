using ChessSchool.Arena.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.Minio;

namespace ChessSchool.Tests;

/// <summary>
/// Реальный раунд-трип S3 против контейнера MinIO (как локальный прод-путь). Проверяет конфиг
/// AWS SDK (path-style, подпись payload), автосоздание приватного бакета, сохранение и чтение.
/// Требует Docker — поэтому в имени WebTests (исключается быстрым фильтром без Docker).
/// </summary>
public class S3ImageStorageWebTests
{
    [Fact]
    public async Task SaveThenOpen_RoundTripsThroughMinio()
    {
        const string user = "minioadmin", pass = "minioadmin";
        await using var minio = new MinioBuilder()
            .WithUsername(user).WithPassword(pass).Build();
        await minio.StartAsync();

        var opts = new S3Options
        {
            ServiceUrl = minio.GetConnectionString(),
            Bucket = "test-broadcasts",
            AccessKey = user,
            SecretKey = pass,
            ForcePathStyle = true,
            CreateBucketIfMissing = true,
        };
        using var storage = new S3ImageStorage(opts, NullLogger<S3ImageStorage>.Instance);

        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        // Save → возвращает прокси-URL (бакет приватный, PublicBaseUrl не задан).
        var url = await storage.SaveAsync(new MemoryStream(bytes), "image/png");
        Assert.StartsWith("/media/broadcasts/", url);
        var key = url["/media/broadcasts/".Length..];
        Assert.True(ImageKinds.IsValidKey(key));

        // Open → те же байты и content-type (создание бакета произошло автоматически).
        var img = await storage.OpenAsync(key);
        Assert.NotNull(img);
        Assert.Equal("image/png", img!.ContentType);
        using var ms = new MemoryStream();
        await img.Content.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());

        // Несуществующий ключ → null (а не исключение).
        Assert.Null(await storage.OpenAsync("ffffffffffffffffffffffffffffffff.png"));
    }
}
