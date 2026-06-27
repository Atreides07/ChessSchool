using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ChessSchool.Arena.Services;

/// <summary>Ошибка переноса внешнего изображения в наше хранилище (показывается админу, сохранение отменяется).</summary>
public sealed class ImageIngestException(string message) : Exception(message);

/// <summary>
/// Переносит изображение, на которое админ дал внешнюю ссылку, в наше хранилище (S3), чтобы сайт не
/// зависел от внешнего источника (картинку нельзя подменить/удалить на чужой стороне).
/// </summary>
public interface IImageIngestor
{
    /// <summary>
    /// Вернуть ссылку на изображение в нашем хранилище для введённого админом URL. Если хранилище не
    /// настроено или URL уже наш (/media или CDN-база) — возвращает URL без изменений. Иначе скачивает
    /// внешнее изображение и кладёт в S3. Бросает <see cref="ImageIngestException"/> при ошибке загрузки.
    /// </summary>
    Task<string?> EnsureStoredAsync(string? url, CancellationToken ct = default);
}

/// <summary>
/// Реализация переноса. Безопасность серверной загрузки произвольного URL (SSRF): только http/https,
/// запрет приватных/loopback/link-local адресов (вкл. метаданные облака 169.254.169.254), ручное
/// следование редиректам с проверкой каждого хоста, лимит размера и content-type, таймаут.
/// </summary>
public class ImageIngestor(
    IImageStorage storage,
    IHttpClientFactory httpFactory,
    ILogger<ImageIngestor> log,
    S3Options? options = null) : IImageIngestor
{
    public const string HttpClientName = "image-ingest";
    private const int MaxRedirects = 3;

    public async Task<string?> EnsureStoredAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        // Без настроенного S3 (dev) переносить некуда — оставляем внешнюю ссылку как есть.
        if (!storage.IsConfigured) return url;

        // Переносим только абсолютные http(s)-ссылки. Всё прочее (относительный /media/... — наш объект)
        // оставляем как есть. Важно: на Unix Uri.TryCreate("/media/..", Absolute) парсит это как file: —
        // поэтому фильтруем по префиксу схемы заранее, а не по результату разбора.
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        // Уже наш объект на CDN-базе (PublicBaseUrl) — не качаем повторно.
        var publicBase = options?.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(publicBase) &&
            url.StartsWith(publicBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return url;

        try
        {
            return await DownloadAndStoreAsync(uri, ct);
        }
        catch (ImageIngestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось перенести изображение {Url} в хранилище.", uri);
            throw new ImageIngestException("Не удалось скачать изображение по ссылке.");
        }
    }

    private async Task<string> DownloadAndStoreAsync(Uri start, CancellationToken ct)
    {
        using var client = CreateClient();
        var current = start;

        for (var hop = 0; ; hop++)
        {
            if (!await IsHostAllowedAsync(current, ct))
                throw new ImageIngestException("Ссылка ведёт на недопустимый (внутренний) адрес.");

            using var resp = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);

            if (IsRedirect(resp.StatusCode))
            {
                if (hop >= MaxRedirects) throw new ImageIngestException("Слишком много перенаправлений.");
                var loc = resp.Headers.Location
                    ?? throw new ImageIngestException("Перенаправление без адреса.");
                current = new Uri(current, loc); // следующий хоп пройдёт ту же проверку хоста
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                throw new ImageIngestException($"Источник вернул статус {(int)resp.StatusCode}.");

            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (!ImageKinds.IsAllowed(contentType))
                throw new ImageIngestException($"Это не поддерживаемое изображение: {contentType ?? "?"}.");

            if (resp.Content.Headers.ContentLength is > ImageKinds.MaxBytes)
                throw new ImageIngestException("Изображение больше допустимого размера.");

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var buffer = await ReadCappedAsync(src, ImageKinds.MaxBytes, ct);
            return await storage.SaveAsync(buffer, contentType!, ct);
        }
    }

    /// <summary>Чтение потока с жёстким лимитом (Content-Length мог отсутствовать/врать) — защита от DoS.</summary>
    private static async Task<MemoryStream> ReadCappedAsync(Stream src, long cap, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                await buffer.DisposeAsync();
                throw new ImageIngestException("Изображение больше допустимого размера.");
            }
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0)
        {
            await buffer.DisposeAsync();
            throw new ImageIngestException("Пустой ответ источника.");
        }
        buffer.Position = 0;
        return buffer;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Клиент без авто-редиректа: редиректы следуем вручную, проверяя хост на каждом хопе.</summary>
    protected virtual HttpClient CreateClient() => httpFactory.CreateClient(HttpClientName);

    /// <summary>SSRF-защита: хост резолвится и все его адреса должны быть публичными.</summary>
    protected virtual async ValueTask<bool> IsHostAllowedAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addrs;
        try { addrs = await Dns.GetHostAddressesAsync(uri.Host, ct); }
        catch (SocketException) { return false; }
        return addrs.Length > 0 && Array.TrueForAll(addrs, ip => !IsPrivate(ip));
    }

    /// <summary>Приватные/служебные диапазоны, недопустимые как цель серверной загрузки.</summary>
    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8
                || b[0] == 10                                  // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 (link-local, метаданные облака)
                || (b[0] == 127)                               // 127.0.0.0/8
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // 100.64.0.0/10 (CGNAT)
                || b[0] >= 224;                                // multicast/reserved
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
            || ip.Equals(IPAddress.IPv6Any)
            || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;       // fc00::/7 unique-local
    }
}
