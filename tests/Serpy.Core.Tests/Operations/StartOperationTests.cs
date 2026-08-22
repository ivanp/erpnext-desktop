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
