using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;

/// <summary>
/// ApplianceService contract tests using a stub store — no QEMU.
/// Focuses on: lifecycle lock busy-detection, status non-mutating, state round-trip.
/// </summary>
public sealed class ApplianceServiceContractTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"SerpySvcTest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private StateStore MakeStore(Action<ApplianceState>? init = null)
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        if (init is not null) store.Mutate(init);
        return store;
    }

    // ── StatusOperation: non-mutating, stale PID detection ───────────────

    [Fact]
    public async Task GetStatus_NotBuilt_ReturnsNotBuiltStopped()
    {
        var store  = MakeStore();
        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(ReadinessState.NotBuilt, status.Readiness);
        Assert.Equal(HealthState.Stopped,     status.Health);
    }

    [Fact]
    public async Task GetStatus_Initialized_ReturnsInitializedStopped()
    {
        var store = MakeStore(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.Health    = HealthState.Stopped;
        });

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(ReadinessState.Initialized, status.Readiness);
        Assert.Equal(HealthState.Stopped,        status.Health);
    }

    [Fact]
    public async Task GetStatus_RunningWithStalePid_ReturnsCrashed()
    {
        var store = MakeStore(s =>
        {
            s.Readiness          = ReadinessState.Initialized;
            s.Health             = HealthState.Running;
            s.QemuPid            = 999999;    // non-existent PID
            s.QemuStartTimeTicks = DateTime.UtcNow.Ticks;
            s.LoopbackUrl        = "http://127.0.0.1:18080";
        });

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(HealthState.Crashed, status.Health);
        Assert.Null(status.LoopbackUrl); // cleared on crash
    }

    [Fact]
    public async Task GetStatus_OpenJournal_SurfacesRecoverOp()
    {
        var store = MakeStore(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.Health    = HealthState.Stopped;
            s.RecoveryJournal = new RecoveryJournal
            {
                TargetImagePath  = "/path/system.qcow2",
                DiskSwapped      = true,
                MariaDbUpgraded  = false,
                HealthPassed     = false,
            };
        });

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(OperationKind.Recover, status.ActiveOperation);
    }

    // ── LifecycleLock busy detection ──────────────────────────────────────

    [Fact]
    public async Task SecondMutation_WhileFirstHeld_ReturnsBusy()
    {
        // Simulate: lifecycle lock is held by a background thread while
        // a second call (on the test thread) tries to acquire it.
        using var lk1 = new LifecycleLock();
        using var lk2 = new LifecycleLock();

        bool busyDetected = false;
        var lease = lk1.TryAcquire(TimeSpan.Zero);

        var t = new Thread(() =>
        {
            var lease2 = lk2.TryAcquire(TimeSpan.Zero);
            busyDetected = lease2 is null;
            lease2?.Dispose();
        });
        t.Start();
        t.Join();

        lease?.Dispose();
        Assert.True(busyDetected);
    }

    // ── State store: Initialized persists across re-reads ─────────────────

    [Fact]
    public void StateStore_WriteInitialized_RoundTrips()
    {
        var store = MakeStore();
        store.Mutate(s =>
        {
            s.Readiness     = ReadinessState.Initialized;
            s.DataImagePath = "/data/data.img";
        });

        var read = store.Read();

        Assert.Equal(ReadinessState.Initialized, read.Readiness);
        Assert.Equal("/data/data.img",           read.DataImagePath);
    }

    // ── Status: built-but-uninitialized never collapses to Initialized ────

    [Fact]
    public async Task GetStatus_Built_ReturnsBuiltNotInitialized()
    {
        var store = MakeStore(s =>
        {
            s.Readiness = ReadinessState.Built;
            s.Health    = HealthState.Stopped;
        });

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(ReadinessState.Built, status.Readiness);
        Assert.NotEqual(ReadinessState.Initialized, status.Readiness);
    }
}

public sealed class ApplianceServicePreconditionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyServicePrecondition-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task BuildAsync_InitializedAppliance_RefusesReplacementOutsideRecover()
    {
        var service = CreateService(ReadinessState.Initialized);

        var result = await service.BuildAsync(NoProgress());

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Contains("recover", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_NotBuiltAppliance_RefusesBeforeCreatingDataDisk()
    {
        var service = CreateService(ReadinessState.NotBuilt);

        var result = await service.InitializeAsync(
            new InitializationParameters("site1.local", "secret"), NoProgress());

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Contains("build", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private ApplianceService CreateService(ReadinessState readiness)
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        store.Mutate(s => s.Readiness = readiness);
        return new ApplianceService(
            null!, null!, null!, null!, null!, null!, store, null!, null!);
    }

    private static IProgress<OperationUpdate> NoProgress() => new Progress<OperationUpdate>();
}

public sealed class ApplianceServiceBuildReadinessTests
{
    [Fact]
    public void CanBuildFrom_BuiltWithoutCommittedData_AllowsRetry()
    {
        var state = new ApplianceState { Readiness = ReadinessState.Built };

        Assert.True(ApplianceService.CanBuildFrom(state, committedDataExists: false));
    }

    [Fact]
    public void CanBuildFrom_NotBuiltWithStateDataPath_RejectsOverwritingPersistentData()
    {
        var state = new ApplianceState
        {
            Readiness = ReadinessState.NotBuilt,
            DataImagePath = "C:\\Serpy\\appliance\\data.img",
        };

        Assert.False(ApplianceService.CanBuildFrom(state, committedDataExists: false));
    }

    [Fact]
    public void CanBuildFrom_NotBuiltWithCommittedDataOnDisk_RejectsOverwritingPersistentData()
    {
        var state = new ApplianceState { Readiness = ReadinessState.NotBuilt };

        Assert.False(ApplianceService.CanBuildFrom(state, committedDataExists: true));
    }

    [Fact]
    public void CanBuildFrom_InitializedWithData_RejectsReplacementOutsideRecover()
    {
        var state = new ApplianceState
        {
            Readiness = ReadinessState.Initialized,
            DataImagePath = "C:\\Serpy\\appliance\\data.img",
        };

        Assert.False(ApplianceService.CanBuildFrom(state, committedDataExists: false));
    }
}
