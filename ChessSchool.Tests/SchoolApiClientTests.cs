using System.Net;
using System.Net.Http.Json;
using ChessSchool.Contracts;
using ChessSchool.Web.Clients;

namespace ChessSchool.Tests;

/// <summary>
/// Клиент доменного API из веб-ЛК. Ключевое: несуществующий ученик (404 от API) НЕ должен ронять
/// страницу 500 — клиент отдаёт null, страница рисует «не найдено».
/// </summary>
public class SchoolApiClientTests
{
    private sealed class StubHandler(HttpStatusCode code, object? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(code);
            if (body is not null) resp.Content = JsonContent.Create(body);
            return Task.FromResult(resp);
        }
    }

    private static SchoolApiClient Client(HttpStatusCode code, object? body) =>
        new(new HttpClient(new StubHandler(code, body)) { BaseAddress = new("https://api.test") });

    [Fact]
    public async Task GetProfile_OnNotFound_ReturnsNull_DoesNotThrow()
    {
        var profile = await Client(HttpStatusCode.NotFound, null).GetProfileAsync(Guid.NewGuid(), "sub");
        Assert.Null(profile);
    }

    [Fact]
    public async Task GetProfile_OnOk_ReturnsProfile()
    {
        var dto = new StudentProfileDto(
            new StudentDto(Guid.NewGuid(), Guid.NewGuid(), "Тест", 1500, 350, 0, 0, 0, 0, null),
            RatingHistory: [], RecentGames: []);
        var profile = await Client(HttpStatusCode.OK, dto).GetProfileAsync(dto.Student.Id, "sub");
        Assert.NotNull(profile);
        Assert.Equal("Тест", profile!.Student.DisplayName);
    }
}
