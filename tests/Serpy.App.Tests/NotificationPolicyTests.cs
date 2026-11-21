using Serpy.App.Platform;
using Serpy.App.Tray;
using Serpy.Core.Contracts;

namespace Serpy.App.Tests;

public sealed class NotificationPolicyTests
{
    [Fact]
    public void ChangeIntoReady_FiresOneReadyNotification()
    {
        var policy = new NotificationPolicy();
        var status = Status(HealthState.Running);

        var first = policy.Evaluate(TrayState.Starting, TrayState.Ready, status);
        Assert.NotNull(first);
        Assert.Equal(NotificationKind.Ready, first.Kind);

        // Staying Ready fires none
        var second = policy.Evaluate(TrayState.Ready, TrayState.Ready, status);
        Assert.Null(second);
    }

    [Fact]
    public void ChangeFromSettingUpToReady_FiresReadyNotification()
    {
        var policy = new NotificationPolicy();
        var status = Status(HealthState.Running);

        var notification = policy.Evaluate(TrayState.SettingUp, TrayState.Ready, status);
        Assert.NotNull(notification);
        Assert.Equal(NotificationKind.Ready, notification.Kind);
    }

    [Fact]
    public void ChangeIntoNeedsAttention_FiresNeedsAttentionNotification()
    {
        var policy = new NotificationPolicy();
        var status = Status(HealthState.Crashed);

        var notification = policy.Evaluate(TrayState.Ready, TrayState.NeedsAttention, status);
        Assert.NotNull(notification);
        Assert.Equal(NotificationKind.NeedsAttention, notification.Kind);
    }

    [Fact]
    public void StayingNeedsAttention_WithSameReason_FiresNone()
    {
        var policy = new NotificationPolicy();
        var status = Status(HealthState.Crashed);

        policy.Evaluate(TrayState.Ready, TrayState.NeedsAttention, status);
        var second = policy.Evaluate(TrayState.NeedsAttention, TrayState.NeedsAttention, status);
        Assert.Null(second);
    }

    [Fact]
    public void NeedsAttention_WithNewReason_FiresNewNotification()
    {
        var policy = new NotificationPolicy();
        var statusCrashed = Status(HealthState.Crashed);
        var statusPassword = new ApplianceStatus(
            ReadinessState.Initialized, HealthState.RunningUnhealthy, null, null, null, null,
            PasswordRejected: true, HealthFailureReason: "password needed");

        policy.Evaluate(TrayState.Ready, TrayState.NeedsAttention, statusCrashed);
        var second = policy.Evaluate(TrayState.NeedsAttention, TrayState.NeedsAttention, statusPassword);
        Assert.NotNull(second);
        Assert.Equal(NotificationKind.NeedsAttention, second.Kind);
    }

    [Fact]
    public void PasswordNeededState_FiresNeedsAttention_NeverReady()
    {
        var policy = new NotificationPolicy();
        var status = new ApplianceStatus(
            ReadinessState.Initialized, HealthState.RunningUnhealthy, null, null, null, null,
            PasswordRejected: true, HealthFailureReason: "password needed");

        var notification = policy.Evaluate(TrayState.Starting, TrayState.NeedsAttention, status);
        Assert.NotNull(notification);
        Assert.Equal(NotificationKind.NeedsAttention, notification.Kind);
    }

    private static ApplianceStatus Status(HealthState health) =>
        new(ReadinessState.Initialized, health, null, null, null, null);
}
