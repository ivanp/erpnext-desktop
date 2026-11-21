using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Health;
using Serpy.Core.Operations;
using Serpy.Core.Setup;

namespace Serpy.Core.Tests.Setup;

public sealed class DetailsReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyDetails-{Guid.NewGuid():N}");

    public DetailsReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheReport_HoldsThePcCheckBothStatesAndTheRunsLogs()
    {
        var log = Path.Combine(_dir, "setup-1.log");
        File.WriteAllLines(log, ["09:00:01  info       Started.", "09:00:02  guest      SERPY_STAGE database"]);

        var text = DetailsReport.Compose(
            "Memory 31.7 GB, needs 8 GB: ok",
            new SetupState { LastFailure = FailureCode.ProvisioningFailed, LastFailedStep = SetupStepId.Build },
            new ApplianceState { Readiness = ReadinessState.NotBuilt },
            null, [log], Now);

        Assert.Contains("Memory 31.7 GB", text, StringComparison.Ordinal);
        Assert.Contains("provisioningFailed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notBuilt", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SERPY_STAGE database", text, StringComparison.Ordinal);
        Assert.Contains("setup-1.log", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReport_NeverHoldsTheSavedCredential_OrAPasswordThatAnOperationLogMasked()
    {
        // The details file reads logs. A log writer masks a registered secret, so the file gets the
        // masked text; the credential store and the certificate store are not inputs at all.
        const string password = "S3cret-admin-password";
        var log = Path.Combine(_dir, "init-1.log");
        using (var writer = new OperationLog(log))
        {
            writer.Redact(password);
            writer.Stage("health", $"Signing in with {password}");
        }

        var credentials = new HealthCredentials(Path.Combine(_dir, ".health-cred"));
        credentials.Store("site1.local", password);

        var text = DetailsReport.Compose(null, new SetupState(), new ApplianceState(), null, [log], Now);

        Assert.DoesNotContain(password, text, StringComparison.Ordinal);
        Assert.Contains("********", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogThatDoesNotExist_IsNamedAsUnreadable_InsteadOfFailingTheReport()
    {
        var text = DetailsReport.Compose(null, new SetupState(), new ApplianceState(), null,
            [Path.Combine(_dir, "missing.log")], Now);

        Assert.Contains("missing.log", text, StringComparison.Ordinal);
        Assert.Contains("could not be read", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVeryLongLog_KeepsItsEnd_WhereTheFailureIs()
    {
        var log = Path.Combine(_dir, "build-1.log");
        File.WriteAllLines(log, Enumerable.Range(0, 6000).Select(i => $"line {i}").Append("SERPY_PROVISION_FAILED database 100"));

        var text = DetailsReport.Compose(null, new SetupState(), new ApplianceState(), null, [log], Now);

        Assert.Contains("SERPY_PROVISION_FAILED database 100", text, StringComparison.Ordinal);
        Assert.Contains("earlier lines left out", text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0\n", text.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALogThatIsStillOpenForWriting_CanBeRead()
    {
        var log = Path.Combine(_dir, "setup-open.log");
        using var writer = new OperationLog(log);
        writer.Info("a line written while the report is composed");

        var text = DetailsReport.Compose(null, new SetupState(), new ApplianceState(), null, [log], Now);

        Assert.Contains("a line written while the report is composed", text, StringComparison.Ordinal);
    }
}
