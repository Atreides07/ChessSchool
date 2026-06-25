using System.Net.Http.Json;
using ChessSchool.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ChessSchool.Tests;

/// <summary>
/// Интеграционные тесты доменного API через WebApplicationFactory: быстрые и детерминированные,
/// каждый прогон — со своей SQLite-БД (изоляция через уникальную строку подключения).
/// </summary>
public class ApiServiceTests : IClassFixture<ApiServiceTests.Factory>
{
    private readonly HttpClient _client;

    public ApiServiceTests(Factory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Students_AreSeeded()
    {
        var students = await _client.GetFromJsonAsync<List<StudentDto>>($"/schools/{Demo.SchoolId}/students");
        Assert.NotNull(students);
        Assert.True(students!.Count >= 5);
        Assert.Contains(students, s => s.DisplayName == "Иван Петров");
    }

    [Fact]
    public async Task CreateStudent_ThenAppearsInList()
    {
        var name = $"Тест-Ученик-{Guid.NewGuid():N}";
        var created = await _client.PostAsJsonAsync($"/schools/{Demo.SchoolId}/students",
            new CreateStudentRequest(Demo.GroupId, name, null));
        created.EnsureSuccessStatusCode();

        var students = await _client.GetFromJsonAsync<List<StudentDto>>($"/schools/{Demo.SchoolId}/students");
        Assert.Contains(students!, s => s.DisplayName == name);
    }

    [Fact]
    public async Task StudentProfile_HasRatingHistory()
    {
        var students = await _client.GetFromJsonAsync<List<StudentDto>>($"/schools/{Demo.SchoolId}/students");
        var profile = await _client.GetFromJsonAsync<StudentProfileDto>($"/students/{students![0].Id}");
        Assert.NotNull(profile);
        Assert.NotEmpty(profile!.RatingHistory);
    }

    public sealed class Factory : WebApplicationFactory<ChessSchool.ApiService.ApiServiceMarker>
    {
        private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"chessschool-test-{Guid.NewGuid():N}.db");

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:school"] = $"Data Source={_dbFile}"
            }));
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbFile)) File.Delete(_dbFile);
        }
    }
}
