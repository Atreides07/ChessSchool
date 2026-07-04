namespace ChessSchool.Web.Clients;

/// <summary>
/// Добавляет заголовок <c>X-Internal-Key</c> ко всем запросам SchoolApiClient (BFF server-to-server).
/// Ключ не попадает в компоненты/разметку — живёт только в handler'е. Публичный <c>/share/{token}</c>
/// эндпоинт ключ игнорирует (он вне защищённой группы ApiService), так что слать его безопасно.
/// </summary>
public sealed class InternalKeyHandler(string key) : DelegatingHandler
{
    public const string HeaderName = "X-Internal-Key";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Remove(HeaderName);
        request.Headers.Add(HeaderName, key);
        return base.SendAsync(request, ct);
    }
}
