using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Health;
using Serpy.Core.Operations;
using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Operations;

public sealed class ResetOperationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"SerpyResetTest-{Guid.NewGuid():N}");
    private readonly string _applianceDir;
    private readonly string _settingsDir;

    public ResetOperationTests()
    {
        _applianceDir = Path.Combine(_dir, "appliance");
        _settingsDir  = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(_applianceDir);
        Directory.CreateDirectory(_settingsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ResetAsync_WhenEmptyDirectory_SucceedsIdempotently()
    {
        var stateStore = new StateStore(Path.Combine(_settingsDir, "state.json"));
        var setupStore = new SetupStateStore(Path.Combine(_settingsDir, "setup-state.json"));
        var creds = new HealthCredentials(Path.Combine(_settingsDir, ".health-cred"));
        var certStore = new TlsCertificateStore(Path.Combine(_settingsDir, "certs"));
        var stopOp = new StopOperation(stateStore, certStore);

        var resetOp = new ResetOperation(stopOp, creds, stateStore, setupStore);
        var progress = new Progress<OperationUpdate>();

        var result = await resetOp.ExecuteAsync(Guid.NewGuid(), progress, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal(OperationKind.Reset, result.Kind);
        Assert.Equal(ReadinessState.NotBuilt, stateStore.Read().Readiness);
        Assert.Equal(HealthState.Stopped, stateStore.Read().Health);
    }

    [Fact]
    public async Task ResetAsync_WipesDisksAndState_WhilePreservingBaseImageAndRuntime()
    {
        var stateStore = new StateStore(Path.Combine(_settingsDir, "state.json"));
        var setupStore = new SetupStateStore(Path.Combine(_settingsDir, "setup-state.json"));
        var creds = new HealthCredentials(Path.Combine(_settingsDir, ".health-cred"));
        var certStore = new TlsCertificateStore(Path.Combine(_settingsDir, "certs"));
        var stopOp = new StopOperation(stateStore, certStore);

        // Pre-populate appliance files in KnownPaths.ApplianceDir
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
        var systemQcow2 = Path.Combine(KnownPaths.ApplianceDir, "system.qcow2");
        var dataImg     = Path.Combine(KnownPaths.ApplianceDir, "data.img");
        var baseImage   = Path.Combine(KnownPaths.ApplianceDir, "debian-base-13.0.0.qcow2");
        var committed   = Path.Combine(KnownPaths.ApplianceDir, ".data-committed");

        File.WriteAllText(systemQcow2, "fake-system");
        File.WriteAllText(dataImg, "fake-data");
        File.WriteAllText(baseImage, "fake-base-image");
        File.WriteAllText(committed, "committed");

        // Pre-populate credentials and state
        creds.Store("erp.test.local", "secretPassword123");
        stateStore.Write(new ApplianceState
        {
            Readiness = ReadinessState.Initialized,
            Health = HealthState.RunningUnhealthy,
            DataImagePath = dataImg,
            SystemImagePath = systemQcow2,
        });

        var resetOp = new ResetOperation(stopOp, creds, stateStore, setupStore);
        var progress = new Progress<OperationUpdate>();

        var result = await resetOp.ExecuteAsync(Guid.NewGuid(), progress, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);

        // Disks wiped
        Assert.False(File.Exists(systemQcow2));
        Assert.False(File.Exists(dataImg));
        Assert.False(File.Exists(committed));

        // Base image preserved
        Assert.True(File.Exists(baseImage));

        // State reset
        var state = stateStore.Read();
        Assert.Equal(ReadinessState.NotBuilt, state.Readiness);
        Assert.Equal(HealthState.Stopped, state.Health);
        Assert.Null(state.DataImagePath);
        Assert.Null(state.SystemImagePath);

        // Credentials cleared
        Assert.Null(creds.Retrieve());

        // Cleanup the test base image
        try { File.Delete(baseImage); } catch { /* ignore */ }
    }
}
