using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

public class ArenaScoringTests
{
    [Fact]
    public void NormalWin_GivesTwo_AndStartsStreak()
    {
        var (score, streak) = ArenaScoring.Apply(0, 0, 1.0);
        Assert.Equal(2, score);
        Assert.Equal(1, streak);
    }

    [Fact]
    public void OnFire_Win_GivesFour()
    {
        // streak=2 → «на огне», следующая победа стоит 4.
        var (score, streak) = ArenaScoring.Apply(10, 2, 1.0);
        Assert.Equal(14, score);
        Assert.Equal(3, streak);
    }

    [Fact]
    public void OnFire_Draw_GivesTwo_KeepsStreak()
    {
        var (score, streak) = ArenaScoring.Apply(10, 2, 0.5);
        Assert.Equal(12, score);
        Assert.Equal(2, streak);
    }

    [Fact]
    public void NormalDraw_GivesOne()
    {
        var (score, _) = ArenaScoring.Apply(0, 0, 0.5);
        Assert.Equal(1, score);
    }

    [Fact]
    public void Loss_ResetsStreak_NoPoints()
    {
        var (score, streak) = ArenaScoring.Apply(10, 3, 0.0);
        Assert.Equal(10, score);
        Assert.Equal(0, streak);
    }
}
