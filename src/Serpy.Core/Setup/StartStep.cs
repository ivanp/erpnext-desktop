using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;

namespace Serpy.Core.Setup;

/// <summary>
/// Starts ERPNext and waits for the functional health check (KTD7). Done means running and healthy
/// already, so a launch after a finished setup does not start a second time.
/// </summary>
public sealed class StartStep(StartOperation start, StateStore stateStore) : ISetupStep
{
    public SetupStepId Id => SetupStepId.Start;
    public int Weight => 15;
    public TimeSpan QuietTime => TimeSpan.FromMinutes(10);

    public bool IsDone(SetupContext context)
    {
        var state = stateStore.Read();
        return state.Health == HealthState.Running &&
               state.QemuPid is { } pid &&
               ProcessIdentity.IsAlive(pid, state.QemuStartTimeTicks ?? 0);
    }

    public async Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        var result = await start.ExecuteAsync(Guid.NewGuid(), context.AsProgress(), ct);
        return SetupStepResult.From(result);
    }

    // Start leaves nothing behind that a later run must delete: a failed start already ends QEMU.
    public void DeleteLeftovers(SetupContext context) { }
}
