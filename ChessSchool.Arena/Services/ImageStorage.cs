namespace ChessSchool.Arena.Services;

/// <summary>Содержимое изображения для отдачи через прокси-эндпоинт /media.</summary>
public sealed record ImageContent(Stream Content, string ContentType, long? Length);

/// <summary>
/// Хранилище фоновых изображений трансляций. Загруженные админом файлы кладутся в общий стор (S3),
/// а не зависят от внешнего URL, который источник может позже подменить на нелегальный контент.
/// Прод — реальный S3; локально — S3-совместимый MinIO (переключение по конфигу Storage:S3).
/// Без настройки — <see cref="NullImageStorage"/> (загрузка недоступна, поле URL остаётся рабочим).
/// </summary>
public interface IImageStorage
{
    /// <summary>Настроено ли хранилище (доступна ли загрузка файлов в админке).</summary>
    bool IsConfigured { get; }

    /// <summary>Сохранить изображение. Возвращает URL для &lt;img src&gt; (как правило /media/broadcasts/{key}).</summary>
    Task<string> SaveAsync(Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Открыть объект по ключу для отдачи через /media. null — нет такого объекта.</summary>
    Task<ImageContent?> OpenAsync(string key, CancellationToken ct = default);
}

/// <summary>Заглушка, когда S3 не настроен: загрузка недоступна (но pasting URL работает).</summary>
public sealed class NullImageStorage : IImageStorage
{
    public bool IsConfigured => false;
    public Task<string> SaveAsync(Stream content, string contentType, CancellationToken ct = default) =>
        throw new InvalidOperationException("Хранилище изображений (S3) не настроено.");
    public Task<ImageContent?> OpenAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<ImageContent?>(null);
}

/// <summary>Настройки S3-хранилища (секция Storage:S3). Прод — реальный S3, dev — MinIO.</summary>
public sealed class S3Options
{
    /// <summary>Endpoint S3-совместимого хранилища (MinIO). Пусто = AWS S3 по региону.</summary>
    public string? ServiceUrl { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string? Region { get; set; }
    /// <summary>Path-style адресация (обязательно для MinIO; для AWS обычно false).</summary>
    public bool ForcePathStyle { get; set; }
    /// <summary>Создавать бакет при отсутствии (dev/MinIO). В проде бакет обычно уже есть.</summary>
    public bool CreateBucketIfMissing { get; set; }
    /// <summary>
    /// Публичная база URL (CDN/публичный бакет) — если задана, ссылки ведут прямо туда (минуя прокси).
    /// Пусто = отдаём через собственный эндпоинт /media (приватный бакет, без mixed-content в dev).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Bucket);
}

/// <summary>
/// Допустимые типы изображений и работа с ключами объектов. Чистые функции — тестируются без S3.
/// Расширение выводится из content-type (имени файла не доверяем).
/// </summary>
public static class ImageKinds
{
    /// <summary>Максимальный размер загружаемого файла (защита от больших фонов и DoS).</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, string> ExtByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
        ["image/avif"] = "avif",
        ["image/gif"] = "gif",
    };

    public static bool IsAllowed(string? contentType) =>
        contentType is not null && ExtByContentType.ContainsKey(contentType);

    public static string Extension(string contentType) => ExtByContentType[contentType];

    public static string ContentTypeForKey(string key)
    {
        var ext = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
        foreach (var (ct, e) in ExtByContentType)
            if (e == ext) return ct;
        return "application/octet-stream";
    }

    private static readonly System.Text.RegularExpressions.Regex KeyRx =
        new("^[0-9a-f]{32}\\.(jpg|png|webp|avif|gif)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Ключ объекта = guid(N).ext — защищает /media от path-traversal и обращения к чужим объектам.</summary>
    public static bool IsValidKey(string key) => KeyRx.IsMatch(key);

    public static string NewKey(string contentType) => $"{Guid.NewGuid():N}.{Extension(contentType)}";
}
