using ChessSchool.Arena.Services;
using ChessSchool.Contracts;

namespace ChessSchool.Tests;

/// <summary>Чистый симулятор завершённого демо-турнира: детерминизм по id + согласованность таблицы.</summary>
public class ArenaFinishedSimulatorTests
{
    [Fact]
    public void Build_IsDeterministicById()
    {
        var a = ArenaFinishedSimulator.Build("demo-arena-1", TimeControl.Bullet, 3600);
        var b = ArenaFinishedSimulator.Build("demo-arena-1", TimeControl.Bullet, 3600);

        Assert.Equal(a.Count, b.Count);
        Assert.Equal(
            a.Select(p => (p.Key, p.Score, p.Games, p.Wins, string.Join(",", p.Results))),
            b.Select(p => (p.Key, p.Score, p.Games, p.Wins, string.Join(",", p.Results))));
    }

    [Fact]
    public void Build_ProducesConsistentStandings()
    {
        var players = ArenaFinishedSimulator.Build("demo-arena-2", TimeControl.Blitz, 3600);

        Assert.InRange(players.Count, 8, 12);
        Assert.All(players, p =>
        {
            Assert.True(p.Games > 0, "у каждого сыграны партии");
            Assert.Equal(p.Games, p.Results.Count);   // история согласована с числом партий
            Assert.True(p.Score >= 0);
            Assert.True(p.Wins <= p.Games);
        });
    }

    [Fact]
    public void Build_DiffersByTournamentId()
    {
        var a = ArenaFinishedSimulator.Build("demo-arena-A", TimeControl.Bullet, 3600);
        var b = ArenaFinishedSimulator.Build("demo-arena-B", TimeControl.Bullet, 3600);
        // Разные id → почти наверняка разные таблицы (разный сид).
        Assert.NotEqual(
            string.Join("|", a.Select(p => $"{p.Key}:{p.Score}")),
            string.Join("|", b.Select(p => $"{p.Key}:{p.Score}")));
    }
}
