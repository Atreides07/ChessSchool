using System.Text;
using ChessSchool.Auth;

namespace ChessSchool.Tests;

/// <summary>TOTP (RFC 6238) и Base32 (RFC 4648) — чистые функции, проверка по контрольным векторам стандартов.</summary>
public class TotpTests
{
    // Контрольные векторы RFC 6238 (Appendix B), SHA-1, seed = ASCII "12345678901234567890", 8 цифр.
    private static readonly byte[] Seed = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void ComputeCode_MatchesRfc6238Vectors(long unixTime, string expected)
    {
        long counter = unixTime / Totp.DefaultPeriodSeconds;
        Assert.Equal(expected, Totp.ComputeCode(Seed, counter, digits: 8));
    }

    [Fact]
    public void Verify_AcceptsCurrentCode_RejectsWrong()
    {
        var now = DateTimeOffset.UtcNow;
        long counter = now.ToUnixTimeSeconds() / Totp.DefaultPeriodSeconds;
        var code = Totp.ComputeCode(Seed, counter); // 6 цифр

        Assert.True(Totp.Verify(Seed, code, now));
        Assert.False(Totp.Verify(Seed, "000000", now) && code == "000000"); // почти наверняка не совпадёт
        Assert.False(Totp.Verify(Seed, "12345", now));   // неверная длина
        Assert.False(Totp.Verify(Seed, "abcdef", now));  // не цифры
        Assert.False(Totp.Verify(Seed, null, now));
    }

    [Fact]
    public void Verify_AcceptsAdjacentWindow()
    {
        var now = DateTimeOffset.UtcNow;
        long counter = now.ToUnixTimeSeconds() / Totp.DefaultPeriodSeconds;
        var prev = Totp.ComputeCode(Seed, counter - 1); // код предыдущего шага
        Assert.True(Totp.Verify(Seed, prev, now, window: 1));
    }

    [Theory]
    [InlineData("foobar", "MZXW6YTBOI")] // RFC 4648 §10
    [InlineData("", "")]
    public void Base32_Encode_MatchesRfc4648(string ascii, string expected)
        => Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(ascii)));

    [Fact]
    public void Base32_RoundTrips()
    {
        var secret = Totp.GenerateSecret();
        Assert.Equal(secret, Base32.Decode(Base32.Encode(secret)));
    }
}
