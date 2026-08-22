using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Versions;

public sealed class VersionEvaluatorTests
{
    [Theory]
    [InlineData("3.14.1", "3.14.1", true)]
    [InlineData("3.14.1", "3.14.0", false)]
    [InlineData("3.14.2", "3.14.1", false)]
    [InlineData("11.8.3-MariaDB-1:11.8.3+maria~deb13", "11.8.3", true)]
    [InlineData("8.0.1",  "8.0.1",  true)]
    public void MatchesLock(string installed, string locked, bool expected) =>
        Assert.Equal(expected, VersionEvaluator.MatchesLock(installed, locked));

    [Theory]
    [InlineData("3.14.1", "3.14.0", true)]
    [InlineData("3.14.0", "3.14.0", true)]
    [InlineData("3.13.9", "3.14.0", false)]
    [InlineData("24.2.0", "24.0.0", true)]
    [InlineData("8.0.1",  "8.0.0",  true)]
    public void MeetsFloor(string installed, string floor, bool expected) =>
        Assert.Equal(expected, VersionEvaluator.MeetsFloor(installed, floor));

    [Theory]
    [InlineData("3.14.1",   "3.14.0",    1)]
    [InlineData("3.14.0",   "3.14.1",   -1)]
    [InlineData("3.14.0",   "3.14.0",    0)]
    [InlineData("10.0.0",   "9.9.9",     1)]   // multi-digit
    [InlineData("3.14.0",   "3.14.0rc2", 1)]   // release > prerelease
    [InlineData("3.14.0rc1","3.14.0rc2",-1)]
    public void Compare_Sign(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(VersionEvaluator.Compare(a, b)));

    [Fact]
    public void Debian_EpochAndSuffix_Stripped() =>
        Assert.True(VersionEvaluator.MatchesLock(
            "11.8.3-MariaDB-1:11.8.3+maria~deb13", "11.8.3"));

    [Fact]
    public void Debian_EpochPrefix_MeetsFloor() =>
        Assert.True(VersionEvaluator.MeetsFloor("1:11.8.3", "11.8.0"));
}
