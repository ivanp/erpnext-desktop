using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Versions;

public sealed class VersionGateTests
{
    private static VersionManifest MakeManifest(
        string pyLock = "3.14.7", string pyFloor = "3.14.0",
        string nodeLock = "24.2.0", string nodeFloor = "24.0.0",
        string dbLock = "11.8.3", string dbFloor = "11.8.0",
        string redisLock = "8.0.1", string redisFloor = "8.0.0",
        string frappeLock = "16.31.0", string erpNextLock = "16.32.3") =>
        new()
        {
            Runtime = new()
            {
                Python  = new() { Lock = pyLock,    Floor = pyFloor },
                Node    = new() { Lock = nodeLock,  Floor = nodeFloor },
                MariaDb = new() { Lock = dbLock,    Floor = dbFloor },
                Redis   = new() { Lock = redisLock, Floor = redisFloor },
            },
            Apps = new()
            {
                Frappe  = new() { Lock = frappeLock,  MinVersion = "16.0.0" },
                ErpNext = new() { Lock = erpNextLock, MinVersion = "16.0.0" },
            },
        };

    private static GuestVersionReport AllGood() => new()
    {
        Python  = "3.14.7",
        Node    = "24.2.0",
        MariaDb = "11.8.3",
        Redis   = "8.0.1",
        Frappe  = "16.31.0",
        ErpNext = "16.32.3",
    };

    [Fact]
    public void AllMatch_Passes()
    {
        // Should not throw.
        VersionGate.Validate(AllGood(), MakeManifest());
    }

    [Theory]
    [InlineData("python",  "3.14.0", "3.14.7")]   // lock mismatch
    [InlineData("python",  "3.15.0", "3.14.7")]   // lock mismatch (newer still rejected)
    [InlineData("nodejs",  "23.9.0", "24.2.0")]   // below floor
    [InlineData("mariadb", "11.7.0", "11.8.3")]   // below floor
    [InlineData("redis",   "7.9.0",  "8.0.1")]    // below floor
    public void Mismatch_ThrowsWithCorrectComponent(
        string component, string installedBad, string lock_)
    {
        var report = AllGood();
        switch (component)
        {
            case "python":  report.Python  = installedBad; break;
            case "nodejs":  report.Node    = installedBad; break;
            case "mariadb": report.MariaDb = installedBad; break;
            case "redis":   report.Redis   = installedBad; break;
        }
        var ex = Assert.Throws<VersionGateException>(
            () => VersionGate.Validate(report, MakeManifest()));

        Assert.Equal(installedBad, ex.Installed);
        Assert.Equal(lock_,        ex.Locked);
        Assert.NotEmpty(ex.Component);
        Assert.NotEmpty(ex.Floor);
    }

    [Fact]
    public void Exception_ContainsAllFourFields()
    {
        var report = AllGood();
        report.Python = "3.13.0"; // below floor 3.14.0
        var ex = Assert.Throws<VersionGateException>(
            () => VersionGate.Validate(report, MakeManifest()));

        Assert.Equal("python",  ex.Component);
        Assert.Equal("3.13.0",  ex.Installed);
        Assert.Equal("3.14.7",  ex.Locked);
        Assert.Equal("3.14.0",  ex.Floor);
    }

    [Fact]
    public void AppLockVPrefix_NormalisesToNumericVersion()
    {
        // Provision uses tag "v16.31.0"; bench version reports "16.31.0".
        // The manifest stores the lock as "16.31.0" (numeric); VersionEvaluator
        // normalises either form so the comparison succeeds.
        var report = AllGood();
        report.Frappe  = "16.31.0";
        report.ErpNext = "16.32.3";
        var manifest = MakeManifest(frappeLock: "v16.31.0", erpNextLock: "v16.32.3");
        VersionGate.Validate(report, manifest); // must not throw
    }

    [Theory]
    [InlineData("frappe", "16.30.0", "16.31.0")]
    [InlineData("erpnext", "16.32.2", "16.32.3")]
    public void AppLock_Mismatch_ThrowsWithComponent(string component, string installed, string locked)
    {
        var report = AllGood();
        if (component == "frappe") report.Frappe = installed;
        else report.ErpNext = installed;

        var ex = Assert.Throws<VersionGateException>(() => VersionGate.Validate(report, MakeManifest()));
        Assert.Equal(component, ex.Component);
        Assert.Equal(installed, ex.Installed);
        Assert.Equal(locked, ex.Locked);
    }

    [Fact]
    public void AppLock_Missing_ThrowsWithMissingInstalledValue()
    {
        var report = AllGood();
        report.Frappe = string.Empty;
        var ex = Assert.Throws<VersionGateException>(() => VersionGate.Validate(report, MakeManifest()));
        Assert.Equal("frappe", ex.Component);
        Assert.Equal("(missing)", ex.Installed);
    }
}
