using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Qemu;

namespace Serpy.Core.Setup;

/// <summary>
/// Prepares the execution environment: installs QEMU and enables required Windows features
/// (HypervisorPlatform and VirtualMachinePlatform) under a single elevation prompt (KTD14, U7).
///
/// If enabling Windows features reports exit code 3010, the step records a restart requirement
/// in <see cref="SetupState"/> and terminates with <see cref="FailureCode.RestartRequired"/>.
/// On resume after reboot, it verifies virtualization via the PC check probe before clearing
/// the pending flag and continuing setup.
/// </summary>
public sealed class PrepareStep(
    ManagedRuntimeResolver resolver,
    SetupStateStore? setupStore = null,
    Func<Task<bool>>? whpxProbe = null) : ISetupStep
{
    public SetupStepId Id => SetupStepId.Prepare;

    public int Weight => 5;

    public TimeSpan QuietTime => TimeSpan.FromMinutes(5);

    public bool IsDone(SetupContext context)
    {
        var state = setupStore?.Read();
        if (state?.RestartRequired == true)
        {
            return false;
        }

        return resolver.IsInstalled() && !context.NeedsFeature;
    }

    public async Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        var state = setupStore?.Read();
        if (state?.RestartRequired == true)
        {
            context.Activity("Checking virtualization after restart");
            var probePassed = !context.NeedsFeature;
            if (probePassed && whpxProbe is not null)
            {
                probePassed = await whpxProbe().ConfigureAwait(false);
            }

            if (probePassed)
            {
                context.Log("Virtualization confirmed active after restart. Clearing restart requirement.");
                setupStore?.Mutate(s => s.RestartRequired = false);

                if (resolver.IsInstalled())
                {
                    return SetupStepResult.Success("QEMU runtime and Windows features are ready.");
                }
            }
            else
            {
                context.Log("Virtualization check failed after restart; a system restart is still required.");
                return SetupStepResult.Failed(
                    FailureCode.RestartRequired,
                    "A restart is required to complete Windows feature setup.");
            }
        }

        var needQemu = !resolver.IsInstalled();
        var needFeature = context.NeedsFeature;

        if (!needQemu && !needFeature)
        {
            context.Log("QEMU is already installed and Windows features are active; skipping elevation.");
            return SetupStepResult.Success("QEMU runtime and Windows features are ready.");
        }

        context.Activity("Setting up QEMU runtime and Windows features");
        context.Log($"Prepare executing: needQemu={needQemu}, needFeature={needFeature}");

        try
        {
            var outcome = await resolver.InstallAsync(
                enableWindowsFeatures: needFeature,
                progress: new Progress<string>(context.Activity),
                ct: ct).ConfigureAwait(false);

            switch (outcome)
            {
                case InstallOutcome.AlreadyInstalled:
                case InstallOutcome.Installed:
                    context.Log("QEMU runtime and Windows features prepared successfully.");
                    return SetupStepResult.Success("QEMU runtime and Windows features are ready.");

                case InstallOutcome.ElevationDeclined:
                    context.Log("Elevation was declined by the user.");
                    return SetupStepResult.Failed(
                        FailureCode.ElevationDeclined,
                        "Administrator permission was not granted.");

                case InstallOutcome.RestartRequired:
                    context.Log("Windows features enabled; a system restart is required.");
                    setupStore?.Mutate(s => s.RestartRequired = true);
                    return SetupStepResult.Failed(
                        FailureCode.RestartRequired,
                        "A restart is required to complete Windows feature setup.");

                default:
                    throw new InvalidOperationException($"Unexpected install outcome: {outcome}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log($"Prepare failed: {ex.Message}");
            return SetupStepResult.Failed(FailureCode.Unknown, ex.Message);
        }
    }

    public void DeleteLeftovers(SetupContext context) { }
}
