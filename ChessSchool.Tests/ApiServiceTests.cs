using System.Net.Http.Json;
using ChessSchool.ApiService.Data;
using ChessSchool.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChessSchool.Tests;

/// <summary>
/// Интеграционные тесты доменного API через WebApplicationFactory: быстрые и детерминированные,
/// без Docker. Боевой провайдер — PostgreSQL; в тестах DbContext подменяется на EF InMemory
/// (каждый прогон — своя изолированная БД по уникальному имени).
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
        private readonly string _dbName = $"chessschool-test-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                // Снимаем боевую (Npgsql) регистрацию контекста.
                services.RemoveAll<DbContextOptions<SchoolDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<SchoolDbContext>();

                // InMemory-провайдер в отдельном internal service provider — чтобы EF-сервисы
                // Npgsql и InMemory не оказались в одном контейнере (EF это запрещает).
                var efProvider = new ServiceCollection()
                    .AddEntityFrameworkInMemoryDatabase()
                    .BuildServiceProvider();

                services.AddDbContext<SchoolDbContext>(o => o
                    .UseInMemoryDatabase(_dbName)
                    .UseInternalServiceProvider(efProvider));
            });
        }
    }
}
