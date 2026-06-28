using ChessSchool.Capacity;

namespace ChessSchool.Tests;

/// <summary>
/// Модель ёмкости онлайн-игры: воспроизводит расчёты CAPACITY_PLANNING (производные нагрузки + число
/// нод). Это математика оценки железа под целевое число игроков (инструмент tools/capacity).
/// </summary>
public class CapacityModelTests
{
    [Fact]
    public void At100k_DerivedLoad_MatchesPlan()
    {
        var s = CapacityModel.Estimate(new CapacityInputs { Players = 100_000 });

        Assert.Equal(50_000, s.ActiveGames);            // 100k игроков → 50k партий
        Assert.Equal(10_000, s.MovesPerSec, 0);         // 50k × 0.2 ход/с
        Assert.Equal(166.7, s.FinishesPerSec, 1);       // 50k / 300 c
        Assert.Equal(2, s.NodesByConnections);          // 100k / 50k
        Assert.InRange(s.GameServerNodes, 3, 4);        // как §4
        Assert.False(s.RedisClusterNeeded);             // ~10k pub/с — один инстанс
        Assert.Equal(1.0, s.StateMemGb, 1);             // 50k × 20КБ
        Assert.Equal(3.0, s.ConnMemGb, 1);              // 100k × 30КБ
    }

    [Fact]
    public void At1M_NeedsManyNodes_AndRedisCluster()
    {
        var s = CapacityModel.Estimate(new CapacityInputs { Players = 1_000_000 });

        Assert.Equal(500_000, s.ActiveGames);
        Assert.Equal(100_000, s.MovesPerSec, 0);
        Assert.InRange(s.GameServerNodes, 24, 32);      // как §5 (≈24–30 + запас)
        Assert.True(s.RedisClusterNeeded);              // 100k pub/с → шардирование
    }

    [Fact]
    public void BotsDisabled_Style_ZeroPlayers_NoNodesNeeded_ByLoad()
    {
        var s = CapacityModel.Estimate(new CapacityInputs { Players = 0 });
        Assert.Equal(0, s.ActiveGames);
        Assert.Equal(0, s.MovesPerSec, 0);
    }

    [Fact]
    public void StrongerHardware_ReducesNodeCount()
    {
        var baseline = CapacityModel.Estimate(new CapacityInputs { Players = 200_000 });
        var bigger = CapacityModel.Estimate(new CapacityInputs { Players = 200_000, ConnectionsPerNode = 100_000 });
        Assert.True(bigger.NodesByConnections < baseline.NodesByConnections);
    }
}
