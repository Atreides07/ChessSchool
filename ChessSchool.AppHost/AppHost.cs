var builder = DistributedApplication.CreateBuilder(args);

// Общий ключ для server-to-server вызовов GameServer → ApiService (архивация партий).
const string internalKey = "dev-internal-key";

// Прод-профиль БД: запускается с `UsePostgres=true` (требует контейнер-рантайма).
// По умолчанию сервисы используют SQLite — локальный запуск без Docker.
var usePostgres = string.Equals(builder.Configuration["UsePostgres"], "true", StringComparison.OrdinalIgnoreCase);

// Отдельный сервис авторизации (IdP) — переиспользуемый, как Google Auth.
var auth = builder.AddProject<Projects.ChessSchool_Auth>("auth")
    .WithEnvironment("InternalApiKey", internalKey);

// Доменный API: школы, ученики, рейтинг, архив партий, шаринг.
var apiService = builder.AddProject<Projects.ChessSchool_ApiService>("apiservice")
    .WithEnvironment("InternalApiKey", internalKey)
    .WithReference(auth);

if (usePostgres)
{
    var postgres = builder.AddPostgres("postgres");
    var authDb = postgres.AddDatabase("auth");
    var schoolDb = postgres.AddDatabase("school");

    auth.WithReference(authDb).WaitFor(authDb).WithEnvironment("Database__Provider", "postgres");
    apiService.WithReference(schoolDb).WaitFor(schoolDb).WithEnvironment("Database__Provider", "postgres");
}

// Игровой сервер: Orleans-силос (живые партии) + SignalR. Валидирует токены IdP,
// архивирует завершённые партии в доменный API.
var gameServer = builder.AddProject<Projects.ChessSchool_GameServer>("gameserver")
    .WithExternalHttpEndpoints() // браузер тонкого JS-клиента подключается к хабу напрямую
    .WithEnvironment("InternalApiKey", internalKey)
    .WithReference(auth)
    .WithReference(apiService)
    .WaitFor(auth)
    .WaitFor(apiService);

// Веб-фронт (Blazor SSR + интерактивная доска).
var web = builder.AddProject<Projects.ChessSchool_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Sso__ClientId", "chessschool-web")
    .WithReference(auth)
    .WithReference(apiService)
    .WithReference(gameServer)
    .WaitFor(apiService)
    .WaitFor(gameServer);

// Сервис Arena (B2C): арена-турниры. Co-hosted Orleans + Blazor, общий аккаунт через SSO.
var arena = builder.AddProject<Projects.ChessSchool_Arena>("arena")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Sso__ClientId", "arena-web")
    .WithReference(auth)
    .WaitFor(auth);

// Регистрируем веб-клиентов в IdP: разрешённый redirect_uri = базовый адрес приложения.
auth.WithEnvironment("Sso__Clients__chessschool-web", web.GetEndpoint("https"));
auth.WithEnvironment("Sso__Clients__arena-web", arena.GetEndpoint("https"));

builder.Build().Run();
