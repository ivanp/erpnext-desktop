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

        // Recovery journal blocks start.
        OperationKind? activeOp = state.ActiveOperation;
        if (state.RecoveryJournal is { HealthPassed: false })
            activeOp = OperationKind.Recover; // surface as active even if no op is running

        var status = new ApplianceStatus(
            Readiness:       state.Readiness,
            Health:          health,
            LoopbackUrl:     health == HealthState.Running ? state.LoopbackUrl : null,
            CurrentStage:    null,
            ActiveOperation: activeOp,
            ProgressPercent: null);

        return Task.FromResult(status);
    }
}
