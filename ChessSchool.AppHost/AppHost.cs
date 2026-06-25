var builder = DistributedApplication.CreateBuilder(args);

// Общий ключ для server-to-server вызовов GameServer → ApiService (архивация партий).
const string internalKey = "dev-internal-key";

// БД — PostgreSQL для всех окружений (dev и прод на одном провайдере, схема через EF-миграции).
// Требует контейнер-рантайм (Docker/Podman): Aspire поднимает контейнер Postgres локально.
// Данные переживают перезапуск (volume), чтобы не терять seeded-клиентов IdP и учеников.
// Имена ресурсов БД отличны от имён проектов (resource-имена Aspire уникальны и case-insensitive:
// "auth" уже занято проектом IdP). Имя ресурса = ключ connection string в сервисе.
var postgres = builder.AddPostgres("postgres").WithDataVolume();
var authDb = postgres.AddDatabase("authdb");
var schoolDb = postgres.AddDatabase("schooldb");

// Отдельный сервис авторизации (IdP) — переиспользуемый, как Google Auth.
var auth = builder.AddProject<Projects.ChessSchool_Auth>("auth")
    .WithEnvironment("InternalApiKey", internalKey)
    .WithReference(authDb)
    .WaitFor(authDb);

// Доменный API: школы, ученики, рейтинг, архив партий, шаринг.
var apiService = builder.AddProject<Projects.ChessSchool_ApiService>("apiservice")
    .WithEnvironment("InternalApiKey", internalKey)
    .WithReference(auth)
    .WithReference(schoolDb)
    .WaitFor(schoolDb);

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
