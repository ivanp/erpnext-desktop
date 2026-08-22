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
