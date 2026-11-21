using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Versions;

public sealed class GuestVersionParserTests
{
    // The strings below are the ones the first real build measured on the guest (attempt 10).
    private const string PythonBanner = "Python 3.14.7";
    private const string NodeBanner = "v24.2.0";
    private const string MariaDbBanner =
        "mariadb from 11.8.3-MariaDB, client 15.2 for debian-linux-gnu (x86_64) using  EditLine wrapper";
    private const string RedisBanner =
        "Redis server v=8.0.2 sha=00000000:0 malloc=jemalloc-5.3.0 bits=64 build=b52b02bf0759f5b4";

    private static VersionManifest Locked() => new()
    {
        Runtime = new VersionManifest.RuntimeSection
        {
            Python = new VersionManifest.LockFloor { Lock = "3.14.7", Floor = "3.14.0" },
            Node = new VersionManifest.LockFloor { Lock = "24.2.0", Floor = "24.0.0" },
            MariaDb = new VersionManifest.LockFloor { Lock = "11.8.3", Floor = "11.8.0" },
            Redis = new VersionManifest.LockFloor { Lock = "8.0.2", Floor = "8.0.0" },
        },
        Apps = new VersionManifest.AppsSection
        {
            Frappe = new VersionManifest.AppEntry { Branch = "v16.31.0", Commit = "x", Lock = "16.31.0", MinVersion = "16.0.0" },
            ErpNext = new VersionManifest.AppEntry { Branch = "v16.32.3", Commit = "y", Lock = "16.32.3", MinVersion = "16.0.0" },
        },
    };

    [Theory]
    [InlineData(PythonBanner, "3.14.7")]
    [InlineData("Python 3.14.0rc2", "3.14.0")]
    [InlineData("Python 3.13.5\n", "3.13.5")]
    public void Python_IsReadFromItsBanner(string output, string expected) =>
        Assert.Equal(expected, GuestVersionParser.Python(output));

    [Theory]
    [InlineData(NodeBanner, "24.2.0")]
    [InlineData("24.2.0", "24.2.0")]
    [InlineData("v22.12.0\n", "22.12.0")]
    public void Node_IsReadFromItsBanner(string output, string expected) =>
        Assert.Equal(expected, GuestVersionParser.Node(output));

    [Theory]
    [InlineData(MariaDbBanner, "11.8.3")]
    [InlineData("mariadb from 11.8.3-MariaDB, client 15.2 for debian-linux-gnu (x86_64)", "11.8.3")]
    [InlineData("mariadb  Ver 15.1 Distrib 10.11.6-MariaDB, for debian-linux-gnu (x86_64) using  EditLine wrapper", "10.11.6")]
    public void MariaDb_IsTheServerVersion_NotTheClientVersion(string output, string expected) =>
        Assert.Equal(expected, GuestVersionParser.MariaDb(output));

    [Theory]
    [InlineData(RedisBanner, "8.0.2")]
    [InlineData("Redis server v=7.2.4 sha=00000000:0 malloc=jemalloc-5.3.0 bits=64 build=abc", "7.2.4")]
    public void Redis_IsReadFromItsBanner(string output, string expected) =>
        Assert.Equal(expected, GuestVersionParser.Redis(output));

    [Fact]
    public void TheMeasuredBanners_PassTheLockedVersionGate()
    {
        // This is the check that failed on the first real build: every banner went to the evaluator raw.
        var report = new GuestVersionReport
        {
            Python = GuestVersionParser.Python(PythonBanner),
            Node = GuestVersionParser.Node(NodeBanner),
            MariaDb = GuestVersionParser.MariaDb(MariaDbBanner),
            Redis = GuestVersionParser.Redis(RedisBanner),
            Frappe = "16.31.0",
            ErpNext = "16.32.3",
        };

        VersionGate.Validate(report, Locked());
    }

    [Fact]
    public void TheRawBannersThemselves_WouldNotHaveMatchedTheLock_WhichIsWhyTheyAreParsedFirst()
    {
        var raw = new GuestVersionReport
        {
            Python = PythonBanner, Node = NodeBanner, MariaDb = MariaDbBanner, Redis = RedisBanner,
            Frappe = "16.31.0", ErpNext = "16.32.3",
        };

        var ex = Assert.Throws<VersionGateException>(() => VersionGate.Validate(raw, Locked()));
        Assert.Equal("python", ex.Component);
    }

    [Fact]
    public void ABannerWithNoVersion_FailsAndQuotesTheOutput_InsteadOfGuessing()
    {
        var ex = Assert.Throws<InvalidDataException>(() => GuestVersionParser.Redis("command not found"));

        Assert.Contains("Redis", ex.Message, StringComparison.Ordinal);
        Assert.Contains("command not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADowngradedMariaDb_StillFailsTheReplacementCheck_OnceBannersAreParsed()
    {
        var installed = new GuestVersionReport { MariaDb = GuestVersionParser.MariaDb(MariaDbBanner), Frappe = "16.31.0", ErpNext = "16.32.3" };
        var replacement = new GuestVersionReport
        {
            MariaDb = GuestVersionParser.MariaDb("mariadb from 11.4.2-MariaDB, client 15.2 for debian-linux-gnu (x86_64)"),
            Frappe = "16.31.0", ErpNext = "16.32.3",
        };

        var ex = Assert.Throws<VersionGateException>(
            () => VersionGate.ValidateReplacementDoesNotDowngrade(replacement, installed));
        Assert.Equal("mariadb", ex.Component);
    }
}
