using ChessSchool.ApiService.Services;
using ChessSchool.Contracts;

namespace ChessSchool.Tests;

public class RatingServiceTests
{
    private readonly IRatingService _rating = new Glicko2RatingService();

    private static PlayerRating P(double rating, double rd = 350, double vol = 0.06) => new(rating, rd, vol);

    [Fact]
    public void Winner_Gains_Loser_Loses()
    {
        var (white, black) = _rating.Compute(P(1500), P(1500), GameResult.WhiteWins);
        Assert.True(white.Delta > 0, "Победитель должен набрать очки.");
        Assert.True(black.Delta < 0, "Проигравший должен потерять очки.");
    }

    [Fact]
    public void Underdog_Win_GainsMoreThanFavorite()
    {
        var underdog = _rating.Compute(P(1400), P(1800), GameResult.WhiteWins).White;
        var favorite = _rating.Compute(P(1800), P(1400), GameResult.WhiteWins).White;
        Assert.True(underdog.Delta > favorite.Delta,
            "Победа более слабого игрока должна давать больше очков.");
    }

    [Fact]
    public void RatingDeviation_Decreases_AfterGame()
    {
        var result = _rating.Compute(P(1500, 350), P(1500, 350), GameResult.Draw);
        Assert.True(result.White.Rd < 350, "После партии неопределённость рейтинга (RD) должна снижаться.");
    }

    [Fact]
    public void Draw_BetweenEqual_KeepsRatingNearlyUnchanged()
    {
        var result = _rating.Compute(P(1500, 50), P(1500, 50), GameResult.Draw);
        Assert.InRange(result.White.Rating, 1495, 1505);
    }

    [Fact]
    public void Volatility_StaysPositive()
    {
        var result = _rating.Compute(P(1500), P(1700), GameResult.BlackWins);
        Assert.True(result.White.Volatility > 0);
        Assert.True(result.Black.Volatility > 0);
    }
}
