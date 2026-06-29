using ChessSchool.Contracts;

namespace ChessSchool.Arena.Services;

/// <summary>Блок таймлайна расписания: позиция в сетке (колонка/строка/протяжённость) + данные для карточки.</summary>
public sealed record ScheduleBlock(
    string Id, int Col, int Span, int Row, string Type, string Title,
    string Time, int Humans, int Bots, string Tc, string State, bool Mine);

/// <summary>
/// Готовая модель главной страницы расписания: блоки таймлайна (вкл. бренд-дорожку) и списки по статусу.
/// Чистый результат сборки — Razor только рендерит, не считает раскладку.
/// </summary>
public sealed record ScheduleView(
    IReadOnlyList<ScheduleBlock> Blocks,
    IReadOnlyList<TournamentSummaryDto> Upcoming,
    IReadOnlyList<TournamentSummaryDto> Running,
    IReadOnlyList<TournamentSummaryDto> Next,
    IReadOnlyList<TournamentSummaryDto> Finished,
    int LiveCount,
    int StartHour,
    long WindowStartUnix);

/// <summary>
/// Сборка модели расписания из списка турниров и бренд-турниров: раскладка таймлайна (колонки по 30 мин,
/// лейны по типу, верхняя бренд-дорожка) и категоризация по статусу. Вынесено из Home.razor как чистая
/// функция — тестируемо и не смешивает логику с разметкой.
/// </summary>
public static class ScheduleBuilder
{
    public const int WindowBackHours = 3;
    public const int Hours = 9; // 3 назад + 6 вперёд (совпадает с окном ArenaDirectoryGrain)

    public static string TypeOf(TimeControl tc) => tc.InitialSeconds switch
    {
        <= 120 => "bullet",
        <= 480 => "blitz",
        _ => "rapid"
    };

    public static string StateOf(TournamentStatus s) => s switch
    {
        TournamentStatus.Running => "live",
        TournamentStatus.Finished => "past",
        _ => "future"
    };

    private static string ShortName(string type) => type switch
    {
        "bullet" => "Bullet",
        "rapid" => "Rapid",
        _ => "Blitz"
    };

    private static int LaneRow(string type) => type switch { "bullet" => 2, "blitz" => 3, _ => 4 };

    public static ScheduleView Build(
        IReadOnlyList<TournamentSummaryDto> tournaments,
        IReadOnlyList<BrandTournamentView> brand,
        DateTimeOffset now)
    {
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset)
            .AddHours(-WindowBackHours);
        int cols = Hours * 2;

        // Бренд-турниры в окне — отдельной верхней дорожкой (row 2); регулярные лейны сдвигаются вниз.
        var brandBlocks = new List<ScheduleBlock>();
        foreach (var v in brand)
        {
            var b = v.Brand;
            int startHalf = (int)Math.Round((b.StartsAt.ToLocalTime() - windowStart).TotalMinutes / 30.0);
            if (startHalf < 0 || startHalf >= cols) continue;
            int span = Math.Min(Math.Max(1, (int)Math.Round(b.DurationSeconds / 1800.0)), cols - startHalf);
            brandBlocks.Add(new ScheduleBlock(
                b.Slug, startHalf + 2, span, 2, "brand", b.Name,
                b.StartsAt.ToLocalTime().ToString("HH:mm"), v.Summary.HumanCount, v.Summary.BotCount,
                new TimeControl(b.InitialSeconds, b.IncrementSeconds).ToString(), StateOf(v.Summary.Status), v.Summary.Joined));
        }
        int laneOffset = brandBlocks.Count > 0 ? 1 : 0;

        var blocks = new List<ScheduleBlock>();
        foreach (var t in tournaments)
        {
            var type = TypeOf(t.TimeControl);
            int startHalf = (int)Math.Round((t.StartsAt.ToLocalTime() - windowStart).TotalMinutes / 30.0);
            if (startHalf < 0 || startHalf >= cols) continue;
            int span = Math.Min(Math.Max(1, (int)Math.Round(t.DurationSeconds / 1800.0)), cols - startHalf);
            blocks.Add(new ScheduleBlock(
                t.Id, startHalf + 2, span, LaneRow(type) + laneOffset, type,
                ShortName(type), t.StartsAt.ToLocalTime().ToString("HH:mm"),
                t.HumanCount, t.BotCount, t.TimeControl.ToString(), StateOf(t.Status), t.Joined));
        }
        blocks.AddRange(brandBlocks);

        return new ScheduleView(
            blocks,
            Upcoming: tournaments.Where(t => t.Status == TournamentStatus.Created).OrderBy(t => t.StartsAt).ToList(),
            Running: tournaments.Where(t => t.Status == TournamentStatus.Running).OrderBy(t => t.StartsAt).ToList(),
            Next: tournaments.Where(t => t.Status == TournamentStatus.Created).OrderBy(t => t.StartsAt).ToList(),
            Finished: tournaments.Where(t => t.Status == TournamentStatus.Finished).OrderByDescending(t => t.StartsAt).ToList(),
            LiveCount: tournaments.Count(t => t.Status == TournamentStatus.Running),
            StartHour: windowStart.Hour,
            WindowStartUnix: windowStart.ToUnixTimeSeconds());
    }
}
