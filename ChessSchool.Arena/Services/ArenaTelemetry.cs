using ChessSchool.Contracts;
using Microsoft.Extensions.Hosting; // IAnalytics

namespace ChessSchool.Arena.Services;

/// <summary>
/// Формирование аналитических событий арены в одном месте (design-review #3): раньше словари событий
/// (`tournament_id`/`time_control` + поля) дублировались в трёх методах грейна — cross-cutting-логика,
/// размазанная по доменным переходам. Схема событий теперь здесь; грейн зовёт по одной строке.
/// </summary>
public sealed class ArenaTelemetry(IAnalytics analytics)
{
    public void Joined(string tournamentId, TimeControl tc, string sub) =>
        analytics.Capture("tournament_joined", sub, Base(tournamentId, tc));

    public void Paired(string tournamentId, TimeControl tc, string sub, bool opponentIsBot, int waitSeconds) =>
        analytics.Capture("arena_paired", sub, With(tournamentId, tc,
            ("opponent_is_bot", opponentIsBot), ("wait_seconds", waitSeconds)));

    public void GameFinished(string tournamentId, TimeControl tc, string sub,
        string outcome, string reason, bool opponentIsBot, bool wasBerserk, int? durationSeconds) =>
        analytics.Capture("arena_game_finished", sub, With(tournamentId, tc,
            ("result", outcome), ("reason", reason), ("opponent_is_bot", opponentIsBot),
            ("was_berserk", wasBerserk), ("duration_seconds", durationSeconds)));

    private static Dictionary<string, object?> Base(string id, TimeControl tc) =>
        new() { ["tournament_id"] = id, ["time_control"] = tc.ToString() };

    private static Dictionary<string, object?> With(string id, TimeControl tc, params (string Key, object? Val)[] extra)
    {
        var d = Base(id, tc);
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }
}
