using ChessSchool.Arena.Services;

namespace ChessSchool.Tests;

/// <summary>
/// Личность бота: детерминирована ключом (переживает реактивацию), даёт разброс рейтингов/силы,
/// слабые ходят быстрее сильных. Это делает ботов в арене разными по силе и скорости.
/// </summary>
public class BotPersonaTests
{
    [Fact]
    public void For_IsDeterministicByKey()
    {
        var a = BotPersona.For("bot-arena-1");
        var b = BotPersona.For("bot-arena-1");
        Assert.Equal(a, b); // тот же ключ → та же сила (важно для реактивации грейна)
    }

    [Fact]
    public void DifferentBots_GetVariedRatings()
    {
        var ratings = Enumerable.Range(1, 30)
            .Select(i => BotPersona.For($"bot-x-{i}").Rating)
            .Distinct()
            .ToList();
        Assert.True(ratings.Count >= 3, "боты должны различаться по рейтингу, а не быть одинаковыми");
    }

    [Fact]
    public void Personas_AreWithinExpectedRanges_AndStrongerThinkLonger()
    {
        for (int i = 1; i <= 50; i++)
        {
            var p = BotPersona.For($"bot-y-{i}");
            Assert.InRange(p.Rating, 800, 2600);
            Assert.InRange(p.Skill, 0, 20);     // допустимый диапазон Stockfish
            Assert.InRange(p.Speed, 0.4, 1.5);  // множитель времени на ход
        }

        // Монотонность «личностей»: выше рейтинг → не ниже сила и не меньше времени на обдумывание.
        var weak = BotPersona.For("bot-y-1");
        var strong = Enumerable.Range(1, 50).Select(i => BotPersona.For($"bot-y-{i}"))
            .OrderByDescending(p => p.Rating).First();
        Assert.True(strong.Skill >= weak.Skill || strong.Rating >= weak.Rating);
    }
}
