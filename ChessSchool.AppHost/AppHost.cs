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
// По одной БД на bounded-контекст (общий Postgres-сервер локально; в проде разводятся строкой
// подключения по серверам). Межконтекстных FK нет — ссылки на игроков по строковому IdP-sub.
var schoolDb = postgres.AddDatabase("schooldb");  // школьный домен (школы/группы/ученики/партии/рейтинг)
var arenaDb = postgres.AddDatabase("arenadb");    // архив арена-партий B2C
var billingDb = postgres.AddDatabase("billingdb"); // подписки/премиум + идемпотентность вебхуков

// Redis — распределённый ярус для мультисервера: SignalR backplane, Orleans clustering/persist,
// общий DataProtection-keyring и ticket-store. Сервисы переключаются на распределённые провайдеры
// при наличии строки подключения "redis" (иначе — dev-путь in-memory/localhost).
var redis = builder.AddRedis("redis").WithDataVolume();

// Seq — локальный сервер структурированных логов/трейсов (OTel). Позволяет проверить «прод-путь»
// наблюдаемости локально: сервисы шлют в него телеметрию при наличии ConnectionStrings:seq.
var seq = builder.AddSeq("seq").WithDataVolume().ExcludeFromManifest();

// MinIO — S3-совместимое объектное хранилище для фоновых изображений трансляций. Позволяет проверить
// прод-путь S3 локально тем же кодом (AWS SDK): в проде — реальный S3, локально — этот контейнер.
// Учётные данные — только для dev. Бакет приватный (отдаём через /media), создаётся приложением.
const string minioUser = "minioadmin";
const string minioPassword = "minioadmin";
var minio = builder.AddContainer("minio", "minio/minio")
    .WithEnvironment("MINIO_ROOT_USER", minioUser)
    .WithEnvironment("MINIO_ROOT_PASSWORD", minioPassword)
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithHttpEndpoint(targetPort: 9001, name: "console")
    .WithVolume("minio-data", "/data");
var minioS3 = minio.GetEndpoint("s3");

// Mailpit — локальная SMTP-ловушка для тестирования почтовых флоу (подтверждение e-mail, сброс пароля):
// принимает всю исходящую почту и показывает её в веб-UI (ссылка «mailpit» в дашборде Aspire, порт 8025).
// SMTP на 1025 (без auth/TLS). Прод-путь тот же код — там задаётся реальный SMTP через конфиг Email:Smtp.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit")
    .WithHttpEndpoint(targetPort: 8025, name: "ui")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp");
var mailSmtp = mailpit.GetEndpoint("smtp");

// Отдельный сервис авторизации (IdP) — переиспользуемый, как Google Auth.
var auth = builder.AddProject<Projects.ChessSchool_Auth>("auth")
    .WithEnvironment("InternalApiKey", internalKey)
    .WithReference(authDb)
    .WithReference(redis) // общий DataProtection-keyring (cookie IdP расшифровывается любой нодой)
    .WithReference(seq)
    // Почта подтверждения e-mail: локально шлём в mailpit (см. дашборд); прод задаёт реальный SMTP конфигом.
    .WithEnvironment("Email__Smtp__Host", mailSmtp.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("Email__Smtp__Port", mailSmtp.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("Email__From", "ChessSchool ID <no-reply@chessschool.local>")
    .WaitFor(authDb)
    .WaitFor(redis)
    .WaitFor(mailpit);

// Доменный API: школы, ученики, рейтинг, архив партий, шаринг.
var apiService = builder.AddProject<Projects.ChessSchool_ApiService>("apiservice")
    .WithEnvironment("InternalApiKey", internalKey)
    .WithReference(auth)
    .WithReference(schoolDb)
    .WithReference(arenaDb)
    .WithReference(billingDb)
    .WithReference(seq)
    // Почта прогресса родителю: локально в mailpit (см. дашборд), прод — реальный SMTP через конфиг Email:Smtp.
    .WithEnvironment("Email__Smtp__Host", mailSmtp.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Host))
    .WithEnvironment("Email__Smtp__Port", mailSmtp.Property(Aspire.Hosting.ApplicationModel.EndpointProperty.Port))
    .WithEnvironment("Email__From", "ChessSchool <no-reply@chessschool.local>")
    .WaitFor(schoolDb)
    .WaitFor(arenaDb)
    .WaitFor(billingDb)
    .WaitFor(mailpit);

// Игровой сервер: Orleans-силос (живые партии) + SignalR. Валидирует токены IdP,
// архивирует завершённые партии в доменный API.
var gameServer = builder.AddProject<Projects.ChessSchool_GameServer>("gameserver")
    .WithExternalHttpEndpoints() // браузер тонкого JS-клиента подключается к хабу напрямую
    .WithEnvironment("InternalApiKey", internalKey)
    .WithReference(auth)
    .WithReference(apiService)
    .WithReference(redis) // SignalR backplane + Orleans clustering между нодами
    .WithReference(seq)
    .WaitFor(auth)
    .WaitFor(apiService)
    .WaitFor(redis);

// Веб-фронт (Blazor SSR + интерактивная доска).
var web = builder.AddProject<Projects.ChessSchool_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Sso__ClientId", "chessschool-web")
    .WithEnvironment("InternalApiKey", internalKey) // BFF: server-to-server вызовы ЛК школы в ApiService
    .WithReference(auth)
    .WithReference(apiService)
    .WithReference(gameServer)
    .WithReference(redis) // общий DataProtection-keyring + распределённый ticket-store (SSO)
    .WithReference(seq)
    .WaitFor(apiService)
    .WaitFor(gameServer)
    .WaitFor(redis);

// Сервис Arena (B2C): арена-турниры. Co-hosted Orleans + Blazor, общий аккаунт через SSO.
var arena = builder.AddProject<Projects.ChessSchool_Arena>("arena")
    .WithExternalHttpEndpoints()
    .WithEnvironment("Sso__ClientId", "arena-web")
    .WithEnvironment("InternalApiKey", internalKey) // entitlement подписки из ApiService (server-to-server)
    .WithReference(auth)
    .WithReference(apiService) // проверка премиум-подписки игрока (/internal/subscriptions/{sub})
    .WithReference(redis) // Orleans clustering+persist турниров, DataProtection, ticket-store
    .WithReference(seq)
    // S3-хранилище фонов трансляций (локально — MinIO; в проде задаётся реальный S3 через конфиг).
    .WithEnvironment("Storage__S3__ServiceUrl", minioS3)
    .WithEnvironment("Storage__S3__Bucket", "broadcasts")
    .WithEnvironment("Storage__S3__AccessKey", minioUser)
    .WithEnvironment("Storage__S3__SecretKey", minioPassword)
    .WithEnvironment("Storage__S3__ForcePathStyle", "true")
    .WithEnvironment("Storage__S3__CreateBucketIfMissing", "true")
    .WaitFor(auth)
    .WaitFor(redis)
    .WaitFor(minio);

// Демо за dev tunnels: если заданы публичные туннельные URL (DemoTunnels:* через user-secrets/env
// AppHost'а), браузер-facing адреса ведут на туннели — redirect_uri клиентов, authority OIDC/JWT всех
// сервисов и хаб /play. Без этих настроек — обычное поведение (внутренние адреса Aspire). См.
// docs/DEMO_TUNNELS.md.
var demoAuth = builder.Configuration["DemoTunnels:Auth"];
var demoWeb = builder.Configuration["DemoTunnels:Web"];
var demoArena = builder.Configuration["DemoTunnels:Arena"];
var demoGame = builder.Configuration["DemoTunnels:GameServer"];

if (!string.IsNullOrWhiteSpace(demoAuth))
{
    // redirect_uri клиентов IdP = публичные адреса приложений.
    auth.WithEnvironment("Sso__Clients__chessschool-web", demoWeb ?? throw new InvalidOperationException("DemoTunnels:Web обязателен в демо-режиме."));
    auth.WithEnvironment("Sso__Clients__arena-web", demoArena ?? throw new InvalidOperationException("DemoTunnels:Arena обязателен в демо-режиме."));
    // authority OIDC (web/arena) и JWT (gameserver) = публичный auth-URL: туда идёт редирект браузера,
    // и с ним должен совпасть issuer токена.
    web.WithEnvironment("Sso__Authority", demoAuth);
    arena.WithEnvironment("Sso__Authority", demoAuth);
    gameServer.WithEnvironment("Sso__Authority", demoAuth);
    // Тонкий клиент /play подключается к публичному хабу gameserver.
    if (!string.IsNullOrWhiteSpace(demoGame)) web.WithEnvironment("GameServer__PublicUrl", demoGame);
}
else
{
    // Обычный режим: redirect_uri = внутренний адрес приложения (динамические порты Aspire).
    auth.WithEnvironment("Sso__Clients__chessschool-web", web.GetEndpoint("https"));
    auth.WithEnvironment("Sso__Clients__arena-web", arena.GetEndpoint("https"));
}

builder.Build().Run();
