using ChessSchool.Auth;

namespace ChessSchool.Tests;

/// <summary>Политика паролей (NIST) и k-anonymity-разбор ответа HIBP — чистые функции, без сети.</summary>
public class PasswordTests
{
    [Theory]
    [InlineData("short", 8, false)]      // 5 < 8
    [InlineData("exactly8", 8, true)]    // ровно 8
    [InlineData("a-long-passphrase-is-fine", 8, true)]
    [InlineData("", 8, false)]
    public void PasswordPolicy_ChecksLength(string password, int min, bool expected)
        => Assert.Equal(expected, PasswordPolicy.IsAcceptable(password, min, out _));

    [Fact]
    public void PasswordPolicy_RejectsTooLong()
        => Assert.False(PasswordPolicy.IsAcceptable(new string('a', PasswordPolicy.MaxLength + 1), 8, out var e) || e != "long");

    [Fact]
    public void PwnedPasswords_HashPrefix_SplitsSha1()
    {
        // SHA-1("password") = 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8
        var (prefix, suffix) = PwnedPasswords.HashPrefix("password");
        Assert.Equal("5BAA6", prefix);
        Assert.Equal("1E4C9B93F3F0682250B6CF8331B7EE68FD8", suffix);
    }

    [Fact]
    public void PwnedPasswords_RangeContains_TrueWhenSuffixPresentWithCount()
    {
        var body = "0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n1E4C9B93F3F0682250B6CF8331B7EE68FD8:99\r\n";
        Assert.True(PwnedPasswords.RangeContains(body, "1E4C9B93F3F0682250B6CF8331B7EE68FD8"));
        Assert.True(PwnedPasswords.RangeContains(body, "1e4c9b93f3f0682250b6cf8331b7ee68fd8")); // регистронезависимо
    }

    [Fact]
    public void PwnedPasswords_RangeContains_FalseWhenAbsentOrPadded()
    {
        var body = "1E4C9B93F3F0682250B6CF8331B7EE68FD8:5\r\nABCDEF0000000000000000000000000000000:0\r\n";
        Assert.False(PwnedPasswords.RangeContains(body, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")); // нет в ответе
        Assert.False(PwnedPasswords.RangeContains(body, "ABCDEF0000000000000000000000000000000")); // padding, count=0
    }
}
