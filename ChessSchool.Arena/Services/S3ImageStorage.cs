using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Services;

/// <summary>
/// Хранилище изображений поверх S3 (реальный AWS S3 в проде, S3-совместимый MinIO локально).
/// Бакет приватный: файлы отдаются браузеру через собственный эндпоинт /media (нет mixed-content и
/// публичной экспозиции бакета). В проде можно задать Storage:S3:PublicBaseUrl (CDN/публичный бакет) —
/// тогда ссылки ведут напрямую, минуя прокси. Объект-ключ = guid(N).ext (генерируется из content-type).
/// </summary>
public sealed class S3ImageStorage : IImageStorage, IDisposable
{
    private readonly S3Options _opt;
    private readonly ILogger<S3ImageStorage> _log;
    private readonly IAmazonS3 _s3;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private bool _bucketReady;

    public S3ImageStorage(S3Options opt, ILogger<S3ImageStorage> log)
    {
        _opt = opt;
        _log = log;

        var config = new AmazonS3Config { ForcePathStyle = _opt.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(_opt.ServiceUrl))
        {
            config.ServiceURL = _opt.ServiceUrl;                 // S3-совместимый endpoint (MinIO)
            config.AuthenticationRegion = _opt.Region ?? "us-east-1";
            // AWS SDK v4 по умолчанию добавляет flexible-checksum (CRC32) + trailer-подпись, что ломает
            // совместимость с MinIO/S3-совместимыми (x-amz-content-sha256 mismatch). Для кастомного
            // endpoint считаем чек-суммы только по требованию; для реального AWS — дефолт (целостность).
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        else if (!string.IsNullOrWhiteSpace(_opt.Region))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_opt.Region);
        }

        // Явные ключи (MinIO/прод-секреты), иначе — стандартная цепочка AWS (роли/окружение).
        _s3 = !string.IsNullOrWhiteSpace(_opt.AccessKey)
            ? new AmazonS3Client(new BasicAWSCredentials(_opt.AccessKey, _opt.SecretKey), config)
            : new AmazonS3Client(config);
    }

    public bool IsConfigured => true;

    public async Task<string> SaveAsync(Stream content, string contentType, CancellationToken ct = default)
    {
        if (!ImageKinds.IsAllowed(contentType))
            throw new InvalidOperationException($"Недопустимый тип изображения: {contentType}.");

        await EnsureBucketAsync(ct);

        // Буферизуем в память (≤5 МБ): S3-загрузка надёжнее с известной длиной и seekable-потоком.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw new InvalidOperationException("Пустой файл.");
        if (buffer.Length > ImageKinds.MaxBytes)
            throw new InvalidOperationException("Файл превышает допустимый размер.");
        buffer.Position = 0;

        var key = ImageKinds.NewKey(contentType);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opt.Bucket,
            Key = key,
            InputStream = buffer, // seekable, известна длина → обычная подпись payload работает и с MinIO по HTTP
            ContentType = contentType,
        }, ct);

        // Прод с CDN/публичным бакетом — прямая ссылка; иначе отдаём через собственный прокси /media.
        return string.IsNullOrWhiteSpace(_opt.PublicBaseUrl)
            ? $"/media/broadcasts/{key}"
            : $"{_opt.PublicBaseUrl!.TrimEnd('/')}/{key}";
    }

    public async Task<ImageContent?> OpenAsync(string key, CancellationToken ct = default)
    {
        if (!ImageKinds.IsValidKey(key)) return null;
        try
        {
            var resp = await _s3.GetObjectAsync(_opt.Bucket, key, ct);
            var contentType = string.IsNullOrWhiteSpace(resp.Headers.ContentType)
                ? ImageKinds.ContentTypeForKey(key)
                : resp.Headers.ContentType;
            return new ImageContent(resp.ResponseStream, contentType,
                resp.ContentLength > 0 ? resp.ContentLength : null);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady) return;
        await _bucketGate.WaitAsync(ct);
        try
        {
            if (_bucketReady) return;
            var exists = await AmazonS3Util.DoesS3BucketExistV2Async(_s3, _opt.Bucket);
            if (!exists)
            {
                if (!_opt.CreateBucketIfMissing)
                    throw new InvalidOperationException($"Бакет '{_opt.Bucket}' не найден, автосоздание отключено.");
                await _s3.PutBucketAsync(new PutBucketRequest { BucketName = _opt.Bucket }, ct);
                _log.LogInformation("Создан бакет изображений '{Bucket}'.", _opt.Bucket);
            }
            _bucketReady = true;
        }
        finally { _bucketGate.Release(); }
    }

    public void Dispose() => _s3.Dispose();
}
