using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;

namespace Serpy.Core.Tests.Operations;

public sealed class StartOperationGuardTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"SerpyStartTest-{Guid.NewGuid():N}");

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

    [Fact]
    public void ReadinessGuard_NotBuilt_ReturnsFailure()
    {
        var store = MakeStore(s => s.Readiness = ReadinessState.NotBuilt);
        var result = new StartGuardDouble(store).Check();
        Assert.NotNull(result);
        Assert.Equal(OperationOutcome.Failure, result!.Outcome);
        Assert.Contains("NotBuilt", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadinessGuard_Built_ReturnsFailure()
    {
        var store = MakeStore(s => { s.Readiness = ReadinessState.Built; s.Health = HealthState.Stopped; });
        var result = new StartGuardDouble(store).Check();
        Assert.NotNull(result);
        Assert.Equal(OperationOutcome.Failure, result!.Outcome);
    }

    [Fact]
    public void RecoveryJournalGuard_OpenJournal_BlocksStart()
    {
        var store = MakeStore(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.Health    = HealthState.Stopped;
            s.RecoveryJournal = new RecoveryJournal
            {
                TargetImagePath      = "/path/system.qcow2",
                TargetMariaDbVersion = "11.8.3",
                TargetFrappeVersion  = "16.0.0",
                DiskSwapped          = true,
                MariaDbUpgraded      = false,
                HealthPassed         = false,
            };
        });
        var result = new StartGuardDouble(store).Check();
        Assert.NotNull(result);
        Assert.Equal(OperationOutcome.Failure, result!.Outcome);
        Assert.Contains("recovery", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadinessGuard_Initialized_PassesGuard()
    {
        var store = MakeStore(s => { s.Readiness = ReadinessState.Initialized; s.Health = HealthState.Stopped; });
        var result = new StartGuardDouble(store).Check();
        Assert.Null(result);
    }
}

public sealed class StartHealthGateTests
{
    [Fact]
    public async Task WaitForHealthyAsync_RetriesUntilFunctionalHealthSucceeds()
    {
        var attempts = 0;

        var result = await StartOperation.WaitForHealthyAsync(
            _ => Task.FromResult(++attempts < 3
                ? Serpy.Core.Health.HealthResult.Fail("ERPNext is still booting", "auth")
                : Serpy.Core.Health.HealthResult.Ok()),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.True(result.Healthy);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WaitForHealthyAsync_DeadlineReturnsLatestFunctionalFailure()
    {
        var result = await StartOperation.WaitForHealthyAsync(
            _ => Task.FromResult(Serpy.Core.Health.HealthResult.Fail("Scheduler is not active", "scheduler")),
            TimeSpan.Zero,
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Equal("scheduler", result.FailedCheck);
    }
}

public sealed class StartCancellationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SerpyStartCancel-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void RecordCancelledStart_ClearsLiveProcessAndEndpointState()
    {
        Directory.CreateDirectory(_directory);
        var store = new StateStore(Path.Combine(_directory, "state.json"));
        store.Mutate(s =>
        {
            s.Health = HealthState.Starting;
            s.QemuPid = 1234;
            s.QemuStartTimeTicks = 5678;
            s.QmpPort = 10001;
            s.QgaPort = 10002;
            s.SerialPort = 10003;
            s.LoopbackUrl = "http://127.0.0.1:18080";
            s.ActiveOperation = OperationKind.Start;
        });

        StartOperation.RecordCancelledStart(store);

        var state = store.Read();
        Assert.Equal(HealthState.Stopped, state.Health);
        Assert.Null(state.QemuPid);
        Assert.Null(state.QemuStartTimeTicks);
        Assert.Null(state.QmpPort);
        Assert.Null(state.QgaPort);
        Assert.Null(state.SerialPort);
        Assert.Null(state.LoopbackUrl);
        Assert.Null(state.ActiveOperation);
    }
}

internal sealed class StartGuardDouble(StateStore stateStore)
{
    public OperationResult? Check()
    {
        var state = stateStore.Read();
        if (state.RecoveryJournal is { HealthPassed: false })
            return new OperationResult(Guid.Empty, OperationKind.Start, OperationOutcome.Failure,
                "A recovery operation was interrupted before the health gate. " +
                "Run Recover to complete migration before starting.");
        if (state.Readiness != ReadinessState.Initialized)
            return new OperationResult(Guid.Empty, OperationKind.Start, OperationOutcome.Failure,
                $"Cannot start: readiness is {state.Readiness}. Build and Initialize first.");
        return null;
    }
}
