using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Запас по размеру заголовков запроса для ВСЕХ сервисов: браузер шлёт auth-cookie
        // на любой порт localhost, и раздутая cookie иначе даёт HTTP 431 на каждом сервисе.
        builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
            o => o.Limits.MaxRequestHeadersTotalSize = 256 * 1024);

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Секрет для server-to-server вызовов (X-Internal-Key). Дефолт-заглушка допустима ТОЛЬКО в
    /// Development: вне его пустой или дефолтный ключ — ошибка старта (иначе любой смог бы подделать
    /// внутренний запрос известным dev-ключом). В проде ключ приходит из env/KMS.
    /// </summary>
    public static string ResolveInternalApiKey(this IConfiguration config, IHostEnvironment env)
    {
        const string devKey = "dev-internal-key";
        var key = config["InternalApiKey"];

        if (!string.IsNullOrWhiteSpace(key))
        {
            if (!env.IsDevelopment() && key == devKey)
                throw new InvalidOperationException(
                    "InternalApiKey равен дефолтному 'dev-internal-key' вне Development. " +
                    "Задайте реальный секрет (env/KMS).");
            return key;
        }

        if (env.IsDevelopment()) return devKey;

        throw new InvalidOperationException(
            "InternalApiKey не задан вне Development. Задайте секрет для server-to-server вызовов (env/KMS).");
    }

    /// <summary>Строка подключения к общему Redis ("redis") или null, если он не сконфигурирован (dev без Redis).</summary>
    public static string? GetRedisConnectionString(this IConfiguration config) =>
        config.GetConnectionString("redis") is { Length: > 0 } c ? c : null;

    /// <summary>
    /// То же, но с fail-fast: вне Development Redis обязателен (распределённые провайдеры — условие
    /// мультисервера), и отсутствие строки подключения роняет старт, а не уводит тихо в single-node
    /// in-memory. В Development допускается null (dev-фолбэк).
    /// </summary>
    public static string? GetRedisConnectionString(this IConfiguration config, IHostEnvironment env)
    {
        var conn = config.GetRedisConnectionString();
        if (conn is null && !env.IsDevelopment())
            throw new InvalidOperationException(
                "ConnectionStrings:redis не задан вне Development. Redis обязателен для мультисервера " +
                "(Orleans clustering/persist/reminders, SignalR backplane, DataProtection, ticket-store).");
        return conn;
    }

    /// <summary>
    /// DataProtection с общим стабильным ApplicationName. При наличии Redis ключи шифрования живут в нём
    /// (любая нода расшифрует cookie/тикеты любой другой) — обязательное условие мультисервера. Без Redis
    /// (dev/одна нода) — стабильный keyring на диске, переживающий перезапуск. Идемпотентно по нодам.
    /// </summary>
    public static IHostApplicationBuilder AddChessSchoolDataProtection(this IHostApplicationBuilder builder)
    {
        var dp = builder.Services.AddDataProtection().SetApplicationName("ChessSchool");
        var redis = builder.Configuration.GetRedisConnectionString(builder.Environment);
        if (redis is not null)
        {
            var mux = ConnectionMultiplexer.Connect(redis);
            dp.PersistKeysToStackExchangeRedis(mux, "chessschool:dataprotection-keys");
        }
        else
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "keys", "dataprotection");
            Directory.CreateDirectory(dir);
            dp.PersistKeysToFileSystem(new DirectoryInfo(dir));
        }
        return builder;
    }

    /// <summary>
    /// Доверие forwarded-заголовкам за обратным прокси/ingress (Aspire локально, ingress в проде):
    /// схема/хост берутся из X-Forwarded-*, чтобы OIDC-issuer и redirect_uri строились по внешнему адресу,
    /// а не по внутреннему порту Kestrel. Нужно всем, кто строит абсолютные URL из запроса (Auth, Web, Arena).
    /// В пайплайне затем вызвать <c>app.UseForwardedHeaders()</c> как можно раньше.
    /// </summary>
    public static IHostApplicationBuilder AddChessSchoolForwardedHeaders(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            // Доверяем инфраструктуре (Aspire/ingress) — иначе пришлось бы перечислять её сети/прокси.
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
        });
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Health-эндпоинты маппятся всегда, чтобы health-check и WaitFor в AppHost работали
        // в любой среде (локальный запуск и интеграционные тесты), а не только в Development.
        // В проде эти пути следует закрывать на уровне сети/ingress — см. https://aka.ms/aspire/healthchecks.

        // All health checks must pass for app to be considered ready to accept traffic after starting
        app.MapHealthChecks(HealthEndpointPath);

        // Only health checks tagged with the "live" tag must pass for app to be considered alive
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        return app;
    }
}
