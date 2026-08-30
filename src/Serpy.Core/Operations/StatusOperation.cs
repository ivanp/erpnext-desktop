using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;

namespace Serpy.Core.Operations;

/// <summary>
/// Non-mutating status read (KTD2, R13).
/// Never acquires the lifecycle lock.
/// Reconciles stale PID: if the recorded QEMU process is gone, marks state Crashed.
/// Reports readiness + health as a single typed ApplianceStatus.
/// </summary>
public sealed class StatusOperation(StateStore stateStore)
{
    public Task<ApplianceStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var state = stateStore.Read();

        // Reconcile: if we think we're Running but the process is gone, mark Crashed.
        var health = state.Health;
        if (health is HealthState.Running or HealthState.RunningUnhealthy or HealthState.Starting)
        {
            if (state.QemuPid.HasValue)
            {
                var alive = ProcessIdentity.IsAlive(
                    state.QemuPid.Value,
                    state.QemuStartTimeTicks ?? 0);

                if (!alive)
                {
                    health = HealthState.Crashed;
                    // Best-effort state update (non-mutating reads shouldn't write,
                    // but crash reconciliation is safe and necessary for UI correctness).
                    stateStore.Mutate(s =>
                    {
                        s.Health = HealthState.Crashed;
                        s.QemuPid = null;
                        s.LoopbackUrl = null;
                    });
                }
            }
        }

        // Journal guards surface active operations even if process dies mid-operation
        OperationKind? activeOp = state.ActiveOperation;
        if (state.RecoveryJournal is { HealthPassed: false })
            activeOp = OperationKind.Recover;
        else if (state.AdoptionJournal is not null)
            activeOp = OperationKind.Adopt;

        string committedDataPath = Path.Combine(KnownPaths.ApplianceDir, "data.img");
        string markerPath = Path.Combine(KnownPaths.ApplianceDir, ".data-committed");
        bool hasCommittedData = File.Exists(committedDataPath) || File.Exists(markerPath) || !string.IsNullOrEmpty(state.DataImagePath);

        bool archiveHasProvenance = File.Exists(committedDataPath) &&
            File.Exists(Images.ProvenanceRecordStore.GetProvenancePath(committedDataPath));

        LaunchRoute route = (state.Readiness == ReadinessState.Initialized)
            ? LaunchRoute.Start
            : LaunchRoute.Setup;

        var status = new ApplianceStatus(
            Readiness:                   state.Readiness,
            Health:                      health,
            LoopbackUrl:                 health == HealthState.Running ? state.LoopbackUrl : null,
            CurrentStage:                null,
            ActiveOperation:             activeOp,
            ProgressPercent:             null,
            HasCommittedDataOnDisk:      hasCommittedData,
            ArchiveHasProvenanceRecord:  archiveHasProvenance,
            RecommendedRoute:            route);
        return Task.FromResult(status);
    }
}
