using System.Text.Json;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Images;
using Serpy.Core.Operations;
using Serpy.Core.Setup;

namespace Serpy.Core.Tests.Operations;

// The service takes the lifecycle lease from the settings folder and the steps read the appliance
// folder, so these tests use a temporary app-data root and never touch a real setup.
[Collection("LifecycleLock")]
public sealed class SetupServiceTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpySetupSvc-{Guid.NewGuid():N}");

    public SetupServiceTests()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_dir, "appdata"));
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Collector : IProgress<SetupUpdate>
    {
        public void Report(SetupUpdate value) { }
    }

    private sealed class StubStep(SetupStepId id, Func<bool> done) : ISetupStep
    {
        public SetupStepId Id => id;
        public int Weight => 10;
        public TimeSpan QuietTime => TimeSpan.FromMinutes(10);
        public int Runs;
        public bool IsDone(SetupContext context) => done();

        public Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(SetupStepResult.Success());
        }

        public void DeleteLeftovers(SetupContext context) { }
    }

    [Fact]
    public async Task ASetupRequest_WhileAnotherOperationHoldsTheLease_ReturnsTheBusyResult_AndRunsNoStep()
    {
        var service = new ApplianceService(null!, null!, null!, null!, null!, null!,
            new StateStore(Path.Combine(_dir, "state.json")), null!, null!);

        using var holder = new LifecycleLock();
        using var lease = holder.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease);

        var result = await service.RunSetupAsync(new SetupRequest(), new Collector());

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Contains("already in progress", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.FailedStep);
    }

    [Fact]
    public async Task TheLeaseIsFree_AgainAfterARun_SoTheNextRequestIsNotBusy()
    {
        var stateStore = new StateStore(Path.Combine(_dir, "state.json"));
        stateStore.Mutate(s => s.Readiness = ReadinessState.Initialized);
        var service = new ApplianceService(null!, null!, null!, null!, null!, null!,
            stateStore, null!, null!);

        // Nothing is built, so the first run fails inside its first step. Either way it must let go.
        await service.RunSetupAsync(new SetupRequest(), new Collector());

        using var holder = new LifecycleLock();
        using var lease = holder.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task ACrashBetweenTheApplianceStateWrite_AndTheSetupStateWrite_EndsAsTheRunWithoutTheCrashDoes()
    {
        // Build finished: the image, its manifest and the appliance state exist. The process died
        // before the setup state recorded the step, so the setup state shows nothing done.
        var image = Path.Combine(KnownPaths.ApplianceDir, "system.qcow2");
        File.WriteAllText(image, "accepted system image");
        File.WriteAllText(Path.Combine(KnownPaths.ApplianceDir, SystemImageManifest.FileName),
            JsonSerializer.Serialize(new SystemImageManifest
            {
                BuildTimestamp = DateTimeOffset.UtcNow,
                ImageSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image))),
            }, ApplianceStateJsonContext.Default.SystemImageManifest));

        var stateStore = new StateStore(Path.Combine(_dir, "state.json"));
        var setupStore = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        Assert.Empty(setupStore.Read().Steps);

        var build = new BuildStep(null!, stateStore);
        var rest = new StubStep(SetupStepId.CompanyData, () => false);
        var op = new SetupOperation([build, rest], setupStore, stateStore, NoPowerRequest.Instance, () => { });

        var result = await op.RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal(SetupStepState.Skipped, setupStore.Read().Steps[SetupStepId.Build]);
        Assert.Equal(ReadinessState.Built, stateStore.Read().Readiness);
        Assert.Equal(1, rest.Runs);
        Assert.True(setupStore.Read().Finished);
    }
}
