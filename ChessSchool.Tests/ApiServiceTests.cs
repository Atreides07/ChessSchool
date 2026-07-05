using System.Net;
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
/// (каждый прогон — своя изолированная БД). ЛК гейтится BFF-моделью (X-Internal-Key + X-Acting-Sub
/// с проверкой владения): хелпер <see cref="Owner"/> шлёт валидный ключ + sub владельца демо-школы.
/// </summary>
public class ApiServiceTests : IClassFixture<ApiServiceTests.Factory>
{
    private const string DevKey = "dev-internal-key";
    private readonly HttpClient _client;

    public ApiServiceTests(Factory factory) => _client = factory.CreateClient();

    // Запрос от владельца демо-школы: internal-key + acting-sub = Demo.OwnerSub.
    private static HttpRequestMessage Owner(HttpMethod method, string url, object? body = null)
    {
        var r = new HttpRequestMessage(method, url);
        r.Headers.Add("X-Internal-Key", DevKey);
        r.Headers.Add("X-Acting-Sub", Demo.OwnerSub);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task<List<StudentDto>> OwnerStudentsAsync()
    {
        var resp = await _client.SendAsync(Owner(HttpMethod.Get, $"/schools/{Demo.SchoolId}/students"));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<StudentDto>>())!;
    }

    [Fact]
    public async Task Students_AreSeeded()
    {
        var students = await OwnerStudentsAsync();
        Assert.True(students.Count >= 5);
        Assert.Contains(students, s => s.DisplayName == "Иван Петров");
    }

    [Fact]
    public async Task CreateStudent_ThenAppearsInList()
    {
        var name = $"Тест-Ученик-{Guid.NewGuid():N}";
        var created = await _client.SendAsync(Owner(HttpMethod.Post, $"/schools/{Demo.SchoolId}/students",
            new CreateStudentRequest(Demo.GroupId, name, null)));
        created.EnsureSuccessStatusCode();

        Assert.Contains(await OwnerStudentsAsync(), s => s.DisplayName == name);
    }

    [Fact]
    public async Task Students_Pagination_LimitsResults()
    {
        var resp = await _client.SendAsync(Owner(HttpMethod.Get, $"/schools/{Demo.SchoolId}/students?take=2"));
        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<List<StudentDto>>();
        Assert.True(page!.Count <= 2);
    }

    [Fact]
    public async Task StudentProfile_HasRatingHistory()
    {
        var students = await OwnerStudentsAsync();
        var resp = await _client.SendAsync(Owner(HttpMethod.Get, $"/students/{students[0].Id}"));
        resp.EnsureSuccessStatusCode();
        var profile = await resp.Content.ReadFromJsonAsync<StudentProfileDto>();
        Assert.NotEmpty(profile!.RatingHistory);
    }

    // ---- Авторизация ЛК (BFF) ----

    [Fact]
    public async Task Domain_WithoutInternalKey_Returns401()
    {
        // Есть acting-sub, но нет internal-ключа → гейт группы отклоняет.
        var req = new HttpRequestMessage(HttpMethod.Get, $"/schools/{Demo.SchoolId}/students");
        req.Headers.Add("X-Acting-Sub", Demo.OwnerSub);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Domain_WithoutActingSub_Returns401()
    {
        // Есть ключ, но не задан действующий пользователь → 401.
        var req = new HttpRequestMessage(HttpMethod.Get, $"/schools/{Demo.SchoolId}/students");
        req.Headers.Add("X-Internal-Key", DevKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Domain_ForeignSchool_Returns403()
    {
        // Валидный ключ + чужой sub на демо-школу → не владелец → 403.
        var req = new HttpRequestMessage(HttpMethod.Get, $"/schools/{Demo.SchoolId}/students");
        req.Headers.Add("X-Internal-Key", DevKey);
        req.Headers.Add("X-Acting-Sub", "some-other-user-sub");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Share_IsAnonymous_NotGated()
    {
        // Публичный share-эндпоинт вне защищённой группы: без ключа/sub → 404 (не 401), значит гейт его не трогает.
        var resp = await _client.GetAsync($"/share/nonexistent-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Share_CreatedByOwner_ReadableAnonymously()
    {
        var students = await OwnerStudentsAsync();
        var shareResp = await _client.SendAsync(Owner(HttpMethod.Post, $"/students/{students[0].Id}/share"));
        shareResp.EnsureSuccessStatusCode();
        var link = await shareResp.Content.ReadFromJsonAsync<ShareLinkDto>();

        // Токен читается без каких-либо заголовков (родитель по ссылке).
        var pub = await _client.GetAsync($"/share/{link!.Token}");
        pub.EnsureSuccessStatusCode();
        Assert.NotNull(await pub.Content.ReadFromJsonAsync<StudentProfileDto>());
    }

    [Fact]
    public async Task Provision_CreatesSchoolForNewOwner_Idempotent()
    {
        var sub = $"owner-{Guid.NewGuid():N}";
        HttpRequestMessage MySchool()
        {
            var r = new HttpRequestMessage(HttpMethod.Get, "/my-school");
            r.Headers.Add("X-Internal-Key", DevKey);
            r.Headers.Add("X-Acting-Sub", sub);
            return r;
        }

        var first = await (await _client.SendAsync(MySchool())).Content.ReadFromJsonAsync<MySchoolDto>();
        Assert.NotEqual(Guid.Empty, first!.SchoolId);
        Assert.NotEqual(Guid.Empty, first.GroupId);

        var second = await (await _client.SendAsync(MySchool())).Content.ReadFromJsonAsync<MySchoolDto>();
        Assert.Equal(first.SchoolId, second!.SchoolId); // идемпотентно — та же школа

        // Новый владелец не видит чужую (демо) школу.
        var foreign = new HttpRequestMessage(HttpMethod.Get, $"/schools/{Demo.SchoolId}/students");
        foreign.Headers.Add("X-Internal-Key", DevKey);
        foreign.Headers.Add("X-Acting-Sub", sub);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(foreign)).StatusCode);
    }

    // ---- Гейт server-to-server: /internal/* закрыт RequireInternalKey ----

    [Fact]
    public async Task Internal_WithoutKey_Returns401()
    {
        var resp = await _client.GetAsync("/internal/subscriptions/some-user");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Internal_WithWrongKey_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/internal/subscriptions/some-user");
        req.Headers.Add("X-Internal-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Internal_WithValidKey_PassesGate()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/internal/subscriptions/some-user");
        req.Headers.Add("X-Internal-Key", DevKey);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(req)).StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<ChessSchool.ApiService.ApiServiceMarker>
    {
        private readonly string _dbName = $"chessschool-test-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                // Подменяем все три bounded-контекста (school/arena/billing) на EF InMemory —
                // иначе арена-эндпоинты (ArenaDbContext) и /internal/subscriptions (BillingDbContext)
                // упадут на резолве Npgsql-строки. У каждого — своя in-memory БД.
                services.RemoveAll<DbContextOptions<SchoolDbContext>>();
                services.RemoveAll<DbContextOptions<ArenaDbContext>>();
                services.RemoveAll<DbContextOptions<BillingDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.RemoveAll<SchoolDbContext>();
                services.RemoveAll<ArenaDbContext>();
                services.RemoveAll<BillingDbContext>();

                var efProvider = new ServiceCollection()
                    .AddEntityFrameworkInMemoryDatabase()
                    .BuildServiceProvider();

                services.AddDbContext<SchoolDbContext>(o => o
                    .UseInMemoryDatabase($"{_dbName}-school")
                    .UseInternalServiceProvider(efProvider));
                services.AddDbContext<ArenaDbContext>(o => o
                    .UseInMemoryDatabase($"{_dbName}-arena")
                    .UseInternalServiceProvider(efProvider));
                services.AddDbContext<BillingDbContext>(o => o
                    .UseInMemoryDatabase($"{_dbName}-billing")
                    .UseInternalServiceProvider(efProvider));
            });
        }
    }
}
