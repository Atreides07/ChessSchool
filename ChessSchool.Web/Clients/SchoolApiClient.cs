using System.Net.Http.Json;
using ChessSchool.Contracts;

namespace ChessSchool.Web.Clients;

/// <summary>
/// Клиент доменного API (ЛК школы, рейтинг, шаринг). BFF: <c>X-Internal-Key</c> добавляет
/// <see cref="InternalKeyHandler"/>, а действующего пользователя (его IdP-`sub` из AuthState) страницы
/// передают параметром <c>actingSub</c> — он уходит в заголовок <c>X-Acting-Sub</c> per-request
/// (не через DefaultRequestHeaders — иначе гонки в конкурентных контурах). ApiService проверяет владение.
/// </summary>
public sealed class SchoolApiClient(HttpClient http)
{
    public const string ActingSubHeader = "X-Acting-Sub";

    private static HttpRequestMessage Req(HttpMethod method, string url, string actingSub, object? body = null)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Add(ActingSubHeader, actingSub);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    /// <summary>Школа текущего пользователя (или создаётся) — вместо фикс. Demo.SchoolId.</summary>
    public async Task<MySchoolDto?> GetOrCreateMySchoolAsync(string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Get, "/my-school", actingSub), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<MySchoolDto>(ct) : null;
    }

    public async Task<IReadOnlyList<StudentDto>> GetStudentsAsync(Guid schoolId, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Get, $"/schools/{schoolId}/students", actingSub), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<List<StudentDto>>(ct) ?? [] : [];
    }

    // Несуществующий/чужой ученик → API отдаёт 404/403; не бросаем (иначе страница падает 500), отдаём null.
    public async Task<StudentProfileDto?> GetProfileAsync(Guid studentId, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Get, $"/students/{studentId}", actingSub), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<StudentProfileDto>(ct) : null;
    }

    // Публичный share-профиль родителю — БЕЗ acting-sub (эндпоинт анонимный).
    public async Task<StudentProfileDto?> GetSharedAsync(string token, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/share/{token}", ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<StudentProfileDto>(ct) : null;
    }

    public async Task<SchoolInsightsDto?> GetInsightsAsync(Guid schoolId, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Get, $"/schools/{schoolId}/insights", actingSub), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<SchoolInsightsDto>(ct) : null;
    }

    public async Task<IReadOnlyList<PendingGameDto>> GetPendingAsync(Guid schoolId, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Get, $"/schools/{schoolId}/pending-games", actingSub), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<List<PendingGameDto>>(ct) ?? [] : [];
    }

    public async Task<IReadOnlyList<GroupDto>> GetGroupsAsync(Guid schoolId, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Get, $"/schools/{schoolId}/groups", actingSub), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<List<GroupDto>>(ct) ?? [] : [];
    }

    public async Task<GroupDto?> CreateGroupAsync(Guid schoolId, string name, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Post, $"/schools/{schoolId}/groups", actingSub, new CreateGroupRequest(name)), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<GroupDto>(ct) : null;
    }

    public async Task<bool> MoveStudentAsync(Guid studentId, Guid groupId, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Post, $"/students/{studentId}/group", actingSub, new MoveStudentRequest(groupId)), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<StudentDto?> CreateStudentAsync(Guid schoolId, CreateStudentRequest req, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Post, $"/schools/{schoolId}/students", actingSub, req), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<StudentDto>(ct) : null;
    }

    public async Task<StudentDto?> UpdateStudentAsync(Guid studentId, UpdateStudentRequest req, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Put, $"/students/{studentId}", actingSub, req), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<StudentDto>(ct) : null;
    }

    public async Task<int> BulkCreateStudentsAsync(Guid schoolId, Guid groupId, IReadOnlyList<string> names, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Post, $"/schools/{schoolId}/students/bulk", actingSub, new BulkCreateStudentsRequest(groupId, names)), ct);
        if (!resp.IsSuccessStatusCode) return 0;
        var created = await resp.Content.ReadFromJsonAsync<List<StudentDto>>(ct);
        return created?.Count ?? 0;
    }

    public Task AttributeAsync(Guid gameId, AttributeGameRequest req, string actingSub, CancellationToken ct = default) =>
        http.SendAsync(Req(HttpMethod.Post, $"/games/{gameId}/attribute", actingSub, req), ct);

    public async Task<ShareLinkDto?> CreateShareAsync(Guid studentId, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Post, $"/students/{studentId}/share", actingSub), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<ShareLinkDto>(ct) : null;
    }

    public async Task<IReadOnlyList<ShareLinkInfoDto>> GetSharesAsync(Guid studentId, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Get, $"/students/{studentId}/shares", actingSub), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<List<ShareLinkInfoDto>>(ct) ?? [] : [];
    }

    public async Task<bool> RevokeShareAsync(Guid studentId, string token, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Post, $"/students/{studentId}/shares/{token}/revoke", actingSub), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<StudentDto?> LinkAccountAsync(Guid studentId, string email, string actingSub, CancellationToken ct = default)
    {
        var resp = await http.SendAsync(Req(HttpMethod.Post, $"/students/{studentId}/link", actingSub, new LinkAccountRequest(email)), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<StudentDto>(ct) : null;
    }
}
