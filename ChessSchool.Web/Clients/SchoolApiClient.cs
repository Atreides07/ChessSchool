using System.Net.Http.Json;
using ChessSchool.Contracts;

namespace ChessSchool.Web.Clients;

/// <summary>Клиент доменного API (ЛК школы, рейтинг, шаринг).</summary>
public sealed class SchoolApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<StudentDto>> GetStudentsAsync(Guid schoolId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<StudentDto>>($"/schools/{schoolId}/students", ct) ?? [];

    public async Task<StudentProfileDto?> GetProfileAsync(Guid studentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<StudentProfileDto>($"/students/{studentId}", ct);

    public async Task<StudentProfileDto?> GetSharedAsync(string token, CancellationToken ct = default)
    {
        var resp = await http.GetAsync($"/share/{token}", ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<StudentProfileDto>(ct) : null;
    }

    public async Task<IReadOnlyList<PendingGameDto>> GetPendingAsync(Guid schoolId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<PendingGameDto>>($"/schools/{schoolId}/pending-games", ct) ?? [];

    public async Task<StudentDto?> CreateStudentAsync(Guid schoolId, CreateStudentRequest req, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"/schools/{schoolId}/students", req, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<StudentDto>(ct) : null;
    }

    public async Task AttributeAsync(Guid gameId, AttributeGameRequest req, CancellationToken ct = default) =>
        await http.PostAsJsonAsync($"/games/{gameId}/attribute", req, ct);

    public async Task<ShareLinkDto?> CreateShareAsync(Guid studentId, CancellationToken ct = default)
    {
        var resp = await http.PostAsync($"/students/{studentId}/share", null, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<ShareLinkDto>(ct) : null;
    }

    public async Task<StudentDto?> LinkAccountAsync(Guid studentId, string email, CancellationToken ct = default)
    {
        var resp = await http.PostAsJsonAsync($"/students/{studentId}/link", new LinkAccountRequest(email), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<StudentDto>(ct) : null;
    }
}
