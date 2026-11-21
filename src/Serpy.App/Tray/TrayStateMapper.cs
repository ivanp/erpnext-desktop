using Serpy.App.Text;
using Serpy.Core.Contracts;

namespace Serpy.App.Tray;

public enum TrayState
{
    SettingUp,
    Starting,
    Ready,
    Stopped,
    NeedsAttention,
}

public sealed record TrayStateInfo(TrayState State, string Tooltip);

/// <summary>Maps the observable appliance and setup status to the tray's five user-facing states.</summary>
public static class TrayStateMapper
{
    public static TrayStateInfo Map(ApplianceStatus status, SetupStepId? currentSetupStep = null)
    {
        if (status.Setup?.Working == true)
        {
            var tooltip = currentSetupStep is { } step
                ? UiText.TraySettingUpStep(step)
                : UiText.TraySettingUp;
            return new(TrayState.SettingUp, tooltip);
        }

        if (status.PasswordRejected ||
            status.Setup?.LastFailure is { } failure && failure != FailureCode.None ||
            status.ActiveOperation is OperationKind.Recover or OperationKind.Adopt ||
            status.Health is HealthState.Crashed or HealthState.RunningUnhealthy)
        {
            return new(TrayState.NeedsAttention, UiText.TrayNeedsAttention);
        }

        if (status.Health == HealthState.Starting ||
            status.ActiveOperation is OperationKind.Start or OperationKind.Restart)
        {
            return new(TrayState.Starting, UiText.TrayStarting);
        }

        if (status.Health == HealthState.Running)
            return new(TrayState.Ready, UiText.TrayReady);

        return new(TrayState.Stopped, UiText.TrayStopped);
    }
}
