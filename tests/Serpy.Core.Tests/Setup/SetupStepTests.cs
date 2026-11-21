using System.Diagnostics;
using System.Text.Json;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Images;
using Serpy.Core.Operations;
using Serpy.Core.Setup;

namespace Serpy.Core.Tests.Setup;

// The steps look for their files in the appliance folder and RecordAcceptedBuild looks for committed
// data there, so every test runs against a temporary app-data root.
[Collection("LifecycleLock")]
public sealed class SetupStepTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpySetupSteps-{Guid.NewGuid():N}");

    public SetupStepTests()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_dir, "appdata"));
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string InAppliance(string name) => Path.Combine(KnownPaths.ApplianceDir, name);

    private StateStore NewStateStore() => new(Path.Combine(_dir, "state.json"));

    private static SetupContext Context(bool journalOpen = false, List<string>? log = null) => new()
    {
        RunId = Guid.NewGuid(),
        JournalOpen = journalOpen,
        Activity = _ => { },
        StepPercent = _ => { },
        Log = line => log?.Add(line),
    };

    /// <summary>Writes a system image and the manifest that BuildOperation writes after its gates pass.</summary>
    private static void WriteAcceptedImage(string content = "system image bytes")
    {
        var image = InAppliance("system.qcow2");
        File.WriteAllText(image, content);
        var manifest = new SystemImageManifest
        {
            BuildTimestamp = DateTimeOffset.UtcNow,
            ImageSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image))),
        };
        File.WriteAllText(InAppliance(SystemImageManifest.FileName),
            JsonSerializer.Serialize(manifest, ApplianceStateJsonContext.Default.SystemImageManifest));
    }

    // ── Build ────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAcceptedSystemImage_MakesTheBuildStepSkip()
    {
        WriteAcceptedImage();
        var store = NewStateStore();
        store.Mutate(s => s.Readiness = ReadinessState.Built);

        Assert.True(new BuildStep(null!, store).IsDone(Context()));
    }

    [Theory]
    [InlineData(".staging-system.qcow2")]
    [InlineData("system.qcow2")]
    public void AnImageWithoutItsAcceptanceManifest_NeverCountsAsABuild(string name)
    {
        File.WriteAllText(InAppliance(name), "bytes");

        Assert.False(new BuildStep(null!, NewStateStore()).IsDone(Context()));
    }

    [Fact]
    public void AnImageThatChangedAfterItWasAccepted_NeverCountsAsABuild()
    {
        WriteAcceptedImage();
        File.AppendAllText(InAppliance("system.qcow2"), "one more byte");

        Assert.False(new BuildStep(null!, NewStateStore()).IsDone(Context()));
    }

    [Fact]
    public void AnAcceptedImage_WithApplianceStateNotBuilt_IsRecordedAsBuilt_WithoutANewBuild()
    {
        WriteAcceptedImage();
        var store = NewStateStore();
        Assert.Equal(ReadinessState.NotBuilt, store.Read().Readiness);
        var log = new List<string>();

        var done = new BuildStep(null!, store).IsDone(Context(log: log));

        Assert.True(done);
        var state = store.Read();
        Assert.Equal(ReadinessState.Built, state.Readiness);
        Assert.Equal(InAppliance("system.qcow2"), state.SystemImagePath);
        Assert.Contains(log, line => line.Contains("recording", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnAcceptedImage_NextToCommittedData_IsRecordedAsInitialized_NotBuilt()
    {
        WriteAcceptedImage();
        File.WriteAllText(InAppliance("data.img"), "company data");
        var store = NewStateStore();

        new BuildStep(null!, store).IsDone(Context());

        Assert.Equal(ReadinessState.Initialized, store.Read().Readiness);
    }

    [Fact]
    public void BuildLeftovers_AreDeleted_ButTheAcceptedImageAndItsManifestAreNot()
    {
        WriteAcceptedImage();
        File.WriteAllText(InAppliance(".staging-system.qcow2"), "half a build");
        File.WriteAllText(InAppliance(".cloud-init-seed.iso"), "seed");

        new BuildStep(null!, NewStateStore()).DeleteLeftovers(Context());

        Assert.False(File.Exists(InAppliance(".staging-system.qcow2")));
        Assert.False(File.Exists(InAppliance(".cloud-init-seed.iso")));
        Assert.True(File.Exists(InAppliance("system.qcow2")));
        Assert.True(File.Exists(InAppliance(SystemImageManifest.FileName)));
    }

    [Fact]
    public void BuildLeftovers_AreKept_WhileAJournalIsOpen()
    {
        File.WriteAllText(InAppliance(".staging-system.qcow2"), "half a build");
        File.WriteAllText(InAppliance(".cloud-init-seed.iso"), "seed");

        new BuildStep(null!, NewStateStore()).DeleteLeftovers(Context(journalOpen: true));

        Assert.True(File.Exists(InAppliance(".staging-system.qcow2")));
        Assert.True(File.Exists(InAppliance(".cloud-init-seed.iso")));
    }

    // ── Company data ─────────────────────────────────────────────────────────

    [Fact]
    public void CommittedData_WithReadinessInitialized_MakesTheCompanyDataStepSkip()
    {
        File.WriteAllText(InAppliance("data.img"), "company data");
        var store = NewStateStore();
        store.Mutate(s => s.Readiness = ReadinessState.Initialized);

        Assert.True(new CompanyDataStep(null!, store).IsDone(Context()));
    }

    [Theory]
    [InlineData(".pending-data.img")]
    [InlineData(".pending-adopted-data.img")]
    public void APendingDisk_NeverCountsAsCompanyData(string name)
    {
        File.WriteAllText(InAppliance(name), "not committed");
        var store = NewStateStore();
        store.Mutate(s => s.Readiness = ReadinessState.Initialized);

        Assert.False(new CompanyDataStep(null!, store).IsDone(Context()));
    }

    [Fact]
    public void CompanyData_WhoseStateSaysNotInitialized_IsNotDone()
    {
        File.WriteAllText(InAppliance("data.img"), "company data");
        var store = NewStateStore();
        store.Mutate(s => s.Readiness = ReadinessState.Built);

        Assert.False(new CompanyDataStep(null!, store).IsDone(Context()));
    }

    [Fact]
    public async Task ExistingCompanyData_IsNeverOverwritten_ByTheCreateNewPath()
    {
        File.WriteAllText(InAppliance("data.img"), "the only copy of the books");
        var store = NewStateStore();
        store.Mutate(s => s.Readiness = ReadinessState.Built);

        // The initialize operation is null: the step must refuse before it would be called.
        var result = await new CompanyDataStep(null!, store).RunAsync(Context(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.DataUnusable, result.Code);
        Assert.Equal("the only copy of the books", File.ReadAllText(InAppliance("data.img")));
    }

    [Fact]
    public void CompanyDataLeftovers_AreDeleted_ButCommittedDataIsNot()
    {
        File.WriteAllText(InAppliance("data.img"), "company data");
        File.WriteAllText(InAppliance(".pending-data.img"), "half initialized");
        File.WriteAllText(InAppliance(".pending-adopted-data.img"), "half adopted");

        new CompanyDataStep(null!, NewStateStore()).DeleteLeftovers(Context());

        Assert.False(File.Exists(InAppliance(".pending-data.img")));
        Assert.False(File.Exists(InAppliance(".pending-adopted-data.img")));
        Assert.True(File.Exists(InAppliance("data.img")));
    }

    [Fact]
    public void CompanyDataLeftovers_AreKept_WhileAJournalIsOpen()
    {
        File.WriteAllText(InAppliance(".pending-adopted-data.img"), "owned by the adoption journal");

        new CompanyDataStep(null!, NewStateStore()).DeleteLeftovers(Context(journalOpen: true));

        Assert.True(File.Exists(InAppliance(".pending-adopted-data.img")));
    }

    // ── Start ────────────────────────────────────────────────────────────────

    [Fact]
    public void ARunningAppliance_WhoseProcessIsAlive_MakesTheStartStepSkip()
    {
        using var self = Process.GetCurrentProcess();
        var store = NewStateStore();
        store.Mutate(s =>
        {
            s.Health = HealthState.Running;
            s.QemuPid = self.Id;
            s.QemuStartTimeTicks = ProcessIdentity.StartTimeTicks(self);
        });

        Assert.True(new StartStep(null!, store).IsDone(Context()));
    }

    [Fact]
    public void ARunningAppliance_WhoseProcessIsGone_IsNotDone()
    {
        var store = NewStateStore();
        store.Mutate(s =>
        {
            s.Health = HealthState.Running;
            s.QemuPid = int.MaxValue;
            s.QemuStartTimeTicks = 1;
        });

        Assert.False(new StartStep(null!, store).IsDone(Context()));
    }

    [Fact]
    public void AnApplianceThatIsStopped_IsNotDone()
    {
        Assert.False(new StartStep(null!, NewStateStore()).IsDone(Context()));
    }
}
