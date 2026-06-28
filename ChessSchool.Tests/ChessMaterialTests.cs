using ChessSchool.Contracts;

namespace ChessSchool.Tests;

/// <summary>
/// Достаточность материала для мата (правило «просрочка времени против недостатка материала = ничья»).
/// Не хватает: одинокий король, K+конь, K+слон. Хватает: пешка/ладья/ферзь или ≥2 лёгких фигуры.
/// </summary>
public class ChessMaterialTests
{
    [Theory]
    // Сторона, у которой НЕ хватает материала на мат → false.
    [InlineData("8/8/8/4k3/8/4K3/8/8 w - - 0 1", true, false)]    // одинокий белый король
    [InlineData("8/8/8/4k3/8/4K3/4N3/8 w - - 0 1", true, false)] // K + конь
    [InlineData("8/8/8/4k3/8/4K3/4B3/8 w - - 0 1", true, false)] // K + слон
    // Сторона, у которой хватает → true.
    [InlineData("8/8/8/4k3/8/4K3/3NN3/8 w - - 0 1", true, true)] // K + 2 коня (мат возможен)
    [InlineData("8/8/8/4k3/8/4K3/3NB3/8 w - - 0 1", true, true)] // K + слон + конь
    [InlineData("8/8/8/4k3/8/4K3/4Q3/8 w - - 0 1", true, true)]  // ферзь
    [InlineData("8/8/8/4k3/8/4K3/4R3/8 w - - 0 1", true, true)]  // ладья
    [InlineData("8/8/8/4k3/8/4K3/4P3/8 w - - 0 1", true, true)]  // пешка (может пройти в ферзи)
    public void HasMatingMaterial_WhiteSide(string fen, bool white, bool expected)
        => Assert.Equal(expected, ChessMaterial.HasMatingMaterial(fen, white));

    [Fact]
    public void StartingPosition_BothSidesHaveMaterial()
    {
        const string start = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        Assert.True(ChessMaterial.HasMatingMaterial(start, white: true));
        Assert.True(ChessMaterial.HasMatingMaterial(start, white: false));
    }

    [Fact]
    public void CountsOnlyRequestedSide()
    {
        // Белые — одинокий король (не хватает); чёрные — ферзь (хватает).
        const string fen = "4k3/4q3/8/8/8/8/8/4K3 w - - 0 1";
        Assert.False(ChessMaterial.HasMatingMaterial(fen, white: true));
        Assert.True(ChessMaterial.HasMatingMaterial(fen, white: false));
    }

    [Fact]
    public void EmptyFen_DefaultsToSufficient_NoAccidentalDraw()
    {
        Assert.True(ChessMaterial.HasMatingMaterial("", white: true));
    }
}
