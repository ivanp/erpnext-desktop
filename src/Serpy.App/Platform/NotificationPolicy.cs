using Serpy.App.Text;
using Serpy.App.Tray;
using Serpy.Core.Contracts;

namespace Serpy.App.Platform;

public enum NotificationKind
{
    Ready,
    NeedsAttention,
}

public sealed record NotificationEvent(NotificationKind Kind, string Title, string Message);

/// <summary>
/// Pure policy deciding from consecutive tray states whether a Windows toast notification fires (KTD19).
/// </summary>
public sealed class NotificationPolicy
{
    private string? _lastNeedsAttentionReason;

    public NotificationEvent? Evaluate(TrayState previous, TrayState current, ApplianceStatus status)
    {
        if (current == TrayState.Ready)
        {
            _lastNeedsAttentionReason = null;
            if (previous != TrayState.Ready)
            {
                return new NotificationEvent(
                    NotificationKind.Ready,
                    UiText.ErpNextIsReady,
                    "Click to open ERPNext in your browser.");
            }
            return null;
        }

        if (current == TrayState.NeedsAttention)
        {
            var reason = ResolveReason(status);
            if (previous != TrayState.NeedsAttention || !string.Equals(_lastNeedsAttentionReason, reason, StringComparison.Ordinal))
            {
                _lastNeedsAttentionReason = reason;
                return new NotificationEvent(
                    NotificationKind.NeedsAttention,
                    UiText.TrayNeedsAttention,
                    reason);
            }
            return null;
        }

        if (current is TrayState.Stopped or TrayState.Starting or TrayState.SettingUp)
        {
            _lastNeedsAttentionReason = null;
        }

        return null;
    }

    private static string ResolveReason(ApplianceStatus status)
    {
        if (status.PasswordRejected)
            return "ERPNext needs your password to sign in.";

        if (status.Setup?.LastFailure is { } failure && failure != FailureCode.None)
            return UiText.Failure(failure).Headline;

        if (status.Health == HealthState.Crashed)
            return "ERPNext stopped unexpectedly.";

        if (status.Health == HealthState.RunningUnhealthy)
            return status.HealthFailureReason ?? "ERPNext did not pass its health check.";

        if (status.ActiveOperation is OperationKind.Recover)
            return "ERPNext was interrupted during recovery.";

        if (status.ActiveOperation is OperationKind.Adopt)
            return "ERPNext was interrupted during adoption.";

        return "Serpy needs your attention.";
    }
}
