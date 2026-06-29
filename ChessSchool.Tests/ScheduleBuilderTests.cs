using ChessSchool.Arena;
using ChessSchool.Arena.Services;
using ChessSchool.Contracts;

namespace ChessSchool.Tests;

/// <summary>Сборка модели расписания (раскладка таймлайна + категоризация), вынесенная из Home.razor.</summary>
public class ScheduleBuilderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static TournamentSummaryDto T(string id, TournamentStatus status, DateTimeOffset starts, int init = 180)
        => new(id, id, new TimeControl(init, 0), status, PlayerCount: 0, SecondsLeft: 0, BotCount: 0, starts, DurationSeconds: 600);

    private static BrandTournamentView Brand(string slug, DateTimeOffset starts) => new(
        new BrandTournament { Slug = slug, Name = slug, StartsAt = starts, DurationSeconds = 600, InitialSeconds = 180 },
        new TournamentSummaryDto(slug, slug, new TimeControl(180, 0), TournamentStatus.Running, 0, 0, 0, starts, 600));

    [Theory]
    [InlineData(60, "bullet")]
    [InlineData(120, "bullet")]
    [InlineData(180, "blitz")]
    [InlineData(480, "blitz")]
    [InlineData(481, "rapid")]
    [InlineData(900, "rapid")]
    public void TypeOf_ClassifiesByInitialSeconds(int init, string expected)
        => Assert.Equal(expected, ScheduleBuilder.TypeOf(new TimeControl(init, 0)));

    [Theory]
    [InlineData(TournamentStatus.Running, "live")]
    [InlineData(TournamentStatus.Finished, "past")]
    [InlineData(TournamentStatus.Created, "future")]
    public void StateOf_MapsStatus(TournamentStatus s, string expected)
        => Assert.Equal(expected, ScheduleBuilder.StateOf(s));

    [Fact]
    public void Build_CategorizesByStatus()
    {
        var view = ScheduleBuilder.Build(
        [
            T("r1", TournamentStatus.Running, Now),
            T("c1", TournamentStatus.Created, Now.AddMinutes(30)),
            T("c2", TournamentStatus.Created, Now.AddMinutes(60)),
            T("f1", TournamentStatus.Finished, Now.AddMinutes(-30)),
        ], [], Now);

        Assert.Single(view.Running);
        Assert.Equal(2, view.Next.Count);
        Assert.Equal(2, view.Upcoming.Count);
        Assert.Single(view.Finished);
        Assert.Equal(1, view.LiveCount);
    }

    [Fact]
    public void Build_ExcludesOutOfWindowFromTimeline_ButKeepsInLists()
    {
        var far = T("far", TournamentStatus.Created, Now.AddHours(100)); // далеко за окном (9ч)
        var view = ScheduleBuilder.Build([far], [], Now);

        Assert.Empty(view.Blocks);          // в таймлайн не попал
        Assert.Single(view.Upcoming);       // но в списке предстоящих есть
    }

    [Fact]
    public void Build_BrandTrackShiftsRegularLanesDown()
    {
        var bullet = T("b", TournamentStatus.Running, Now, init: 60);

        var noBrand = ScheduleBuilder.Build([bullet], [], Now);
        var bulletRowNoBrand = noBrand.Blocks.Single(b => b.Id == "b").Row;

        var withBrand = ScheduleBuilder.Build([bullet], [Brand("major", Now)], Now);
        var bulletRowWithBrand = withBrand.Blocks.Single(b => b.Id == "b").Row;

        Assert.Equal(2, bulletRowNoBrand);                 // bullet-лейн = row 2 без бренда
        Assert.Equal(3, bulletRowWithBrand);               // сдвинут вниз на 1 из-за бренд-дорожки
        Assert.Contains(withBrand.Blocks, b => b.Type == "brand" && b.Row == 2); // бренд занял верхнюю дорожку
    }
}
