using System.Diagnostics.Metrics;
using ChessSchool.Auth.Data;

namespace ChessSchool.Auth;

/// <summary>
/// Метрики auth-событий для наблюдаемости и алертинга: счётчик с тегом типа события экспортируется через
/// OpenTelemetry (см. AddMeter в Program.cs). На нём в проде строятся дашборды и пороговые алерты — всплеск
/// <c>LoginFailure</c> (перебор), рост <c>NewDeviceLogin</c> (компрометация) и т.п. Дополняет табличный аудит
/// (<see cref="AuthAudit"/>): аудит хранит детали для расследования, метрика — дёшево агрегируется для алертов.
/// </summary>
public static class AuthMetrics
{
    public const string MeterName = "ChessSchool.Auth";
    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> EventsCounter = Meter.CreateCounter<long>(
        "chessschool.auth.events", unit: "{event}", description: "Число auth-событий по типу");

    public static void Record(AuthEventType type) =>
        EventsCounter.Add(1, new KeyValuePair<string, object?>("type", type.ToString()));
}
