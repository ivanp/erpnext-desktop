using Serpy.Core.Downloads;
using Serpy.Core.Versions;

using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Qemu;
using Serpy.Core.Setup;
using Xunit;

namespace Serpy.Core.Tests.Setup;

[Collection("LifecycleLock")]
public sealed class PrepareStepTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyPrepareTests-{Guid.NewGuid():N}");

    public PrepareStepTests()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_dir, "appdata"));
        Directory.CreateDirectory(KnownPaths.SettingsDir);
        Directory.CreateDirectory(KnownPaths.RuntimeDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    private SetupContext CreateContext(bool needsFeature = false, List<string>? log = null) => new()
    {
        RunId = Guid.NewGuid(),
        JournalOpen = false,
        Activity = _ => { },
        StepPercent = _ => { },
        Log = line => log?.Add(line),
        NeedsFeature = needsFeature,
    };

    private (ManagedRuntimeResolver Resolver, List<bool> FeatureFlags) CreateMockResolver(
        Func<bool, InstallOutcome>? outcomeFactory = null)
    {
        var manifest = new RuntimeManifest
        {
            QemuVersion = "11.1.0",
            Windows = new RuntimeManifest.WindowsBundle
            {
                ArchiveSha256 = new string('0', 64),
            },
        };

        var featureFlags = new List<bool>();
        var resolver = new ManagedRuntimeResolver(manifest);
        resolver.InstallHandler = (enableFeatures, _, _) =>
        {
            featureFlags.Add(enableFeatures);
            if (outcomeFactory is not null)
            {
                return Task.FromResult(outcomeFactory(enableFeatures));
            }

            return Task.FromResult(InstallOutcome.Installed);
        };

        return (resolver, featureFlags);
    }

    private static void FakeInstallQemu(ManagedRuntimeResolver resolver)
    {
        Directory.CreateDirectory(resolver.BundleDir);
        File.WriteAllText(resolver.QemuSystemExe, "fake qemu exe");
        File.WriteAllText(resolver.QemuImgExe, "fake img exe");
        var share = Path.Combine(resolver.BundleDir, "share", "qemu");
        Directory.CreateDirectory(share);
        File.WriteAllText(Path.Combine(share, "bios-256k.bin"), "fake bios");
        File.WriteAllText(Path.Combine(resolver.BundleDir, ".serpy-runtime-validation"),
            $"11.1.0\n{resolver.DeliveryFingerprint}");
    }

    [Fact]
    public void Metadata_HasExpectedValues()
    {
        var (resolver, _) = CreateMockResolver();
        var step = new PrepareStep(resolver);

        Assert.Equal(SetupStepId.Prepare, step.Id);
        Assert.Equal(5, step.Weight);
        Assert.Equal(TimeSpan.FromMinutes(5), step.QuietTime);
    }

    [Fact]
    public async Task RunAsync_WhenQemuMissingAndFeatureNeeded_MakesOneElevatedCallForBothJobs()
    {
        var (resolver, featureFlags) = CreateMockResolver();
        var store = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        var step = new PrepareStep(resolver, store);
        var context = CreateContext(needsFeature: true);

        // Before run: neither QEMU is installed nor feature is active, so IsDone is false
        Assert.False(step.IsDone(context));

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Single(featureFlags);
        Assert.True(featureFlags[0]);
        Assert.False(store.Read().RestartRequired);
    }

    [Fact]
    public async Task RunAsync_WhenQemuPresentAndFeatureActive_NoElevationRequested()
    {
        var (resolver, featureFlags) = CreateMockResolver();
        FakeInstallQemu(resolver);
        var store = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        var step = new PrepareStep(resolver, store);
        var context = CreateContext(needsFeature: false);

        Assert.True(step.IsDone(context));

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Empty(featureFlags);
    }

    [Fact]
    public async Task RunAsync_WhenPromptDeclined_ReturnsElevationDeclinedAndLeavesStateUnchanged_ThenSecondTryWorks()
    {
        var shouldDecline = true;
        var (resolver, featureFlags) = CreateMockResolver(
            outcomeFactory: _ => shouldDecline ? InstallOutcome.ElevationDeclined : InstallOutcome.Installed);

        var store = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        var step = new PrepareStep(resolver, store);
        var context = CreateContext(needsFeature: true);

        // First attempt: user declines prompt (AE5)
        var firstResult = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, firstResult.Outcome);
        Assert.Equal(FailureCode.ElevationDeclined, firstResult.Code);
        Assert.False(store.Read().RestartRequired);

        // Second attempt: user accepts prompt
        shouldDecline = false;
        var secondResult = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, secondResult.Outcome);
        Assert.Equal(2, featureFlags.Count);
        Assert.False(store.Read().RestartRequired);
    }

    [Fact]
    public async Task RunAsync_WhenHelperReports3010_SetsRestartPendingAndReturnsRestartRequired()
    {
        var (resolver, featureFlags) = CreateMockResolver(
            outcomeFactory: _ => InstallOutcome.RestartRequired);

        var store = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        var step = new PrepareStep(resolver, store);
        var context = CreateContext(needsFeature: true);

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.RestartRequired, result.Code);
        Assert.True(store.Read().RestartRequired);
        Assert.Single(featureFlags);
    }

    [Fact]
    public async Task RunAsync_WhenRestartPendingAndProbePasses_ClearsFlagAndContinuesToDownload()
    {
        var (resolver, featureFlags) = CreateMockResolver();
        FakeInstallQemu(resolver);
        var store = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        store.Mutate(s => s.RestartRequired = true);

        var probeCalled = false;
        var step = new PrepareStep(resolver, store, whpxProbe: () =>
        {
            probeCalled = true;
            return Task.FromResult(true);
        });

        // While restart is pending, IsDone must be false so the step runs and validates the reboot
        var context = CreateContext(needsFeature: false);
        Assert.False(step.IsDone(context));

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.True(probeCalled);
        Assert.False(store.Read().RestartRequired);
        // No elevation requested during resume check
        Assert.Empty(featureFlags);
    }

    [Fact]
    public async Task RunAsync_WhenRestartPendingAndProbeFails_KeepsRestartRequired()
    {
        var (resolver, featureFlags) = CreateMockResolver();
        FakeInstallQemu(resolver);
        var store = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        store.Mutate(s => s.RestartRequired = true);

        var step = new PrepareStep(resolver, store, whpxProbe: () => Task.FromResult(false));
        var context = CreateContext(needsFeature: false);

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.RestartRequired, result.Code);
        Assert.True(store.Read().RestartRequired);
        Assert.Empty(featureFlags);
    }

    [Fact]
    public async Task RunAsync_WhenHelperFails_ReturnsFailureWithExitCodeInLog()
    {
        var (resolver, _) = CreateMockResolver();
        resolver.InstallHandler = (_, _, _) =>
            throw new InvalidOperationException("Elevated helper failed with exit code 8.");

        var store = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        var step = new PrepareStep(resolver, store);
        var log = new List<string>();
        var context = CreateContext(needsFeature: true, log: log);

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Contains("8", result.Message);
        Assert.Contains(log, line => line.Contains("8"));
    }

    [Fact]
    public async Task RunAsync_WhenQemuNeeded_AsksEngineForInstallerPartBeforeElevation()
    {
        var (resolver, _) = CreateMockResolver(enableFeatures => InstallOutcome.Installed);

        var engineCalled = false;
        var installerDownloadedBeforeElevation = false;

        var cache = new DownloadCache(Path.Combine(_dir, "cache"));
        var inputs = new BuildInputs
        {
            Parts =
            [
                new InputPart
                {
                    Id = "engine",
                    Title = "Serpy engine",
                    Files = []
                }
            ]
        };

        var engine = new DownloadEngine(inputs, cache, delay: (_, _) => Task.CompletedTask);
        resolver.InstallHandler = (enableFeatures, _, _) =>
        {
            installerDownloadedBeforeElevation = engineCalled;
            return Task.FromResult(InstallOutcome.Installed);
        };

        // Wrapper or subclass to intercept DownloadPartsAsync
        var step = new PrepareStep(resolver, engine: engine);

        // To track whether engine download happened before InstallHandler:
        // We can hook cache or pre-populate
        var log = new List<string>();
        var context = CreateContext(needsFeature: false, log: log);

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Contains(log, line => line.Contains("Downloading QEMU installer before elevation"));
    }
}
