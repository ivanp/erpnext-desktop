using Serpy.App.Tray;
using Serpy.Core.Contracts;

namespace Serpy.App.Tests;

public sealed class TrayStateMapperTests
{
    [Fact]
    public void WorkingSetupMapsToSettingUpWithFiveStageProgress()
    {
        var status = Status(setup: new SetupSummary(false, true, FailureCode.None));

        var result = TrayStateMapper.Map(status, SetupStepId.Build);

        Assert.Equal(TrayState.SettingUp, result.State);
        Assert.Equal("Serpy: setting up, step 3 of 5", result.Tooltip);
    }

    [Fact]
    public void FailedSetupMapsToNeedsAttention()
    {
        var status = Status(setup: new SetupSummary(false, false, FailureCode.ProvisioningFailed));

        var result = TrayStateMapper.Map(status);

        Assert.Equal(TrayState.NeedsAttention, result.State);
        Assert.Equal("Serpy: needs attention", result.Tooltip);
    }

    [Fact]
    public void BlockingRecoveryOperationMapsToNeedsAttention()
    {
        var result = TrayStateMapper.Map(Status(operation: OperationKind.Recover));

        Assert.Equal(TrayState.NeedsAttention, result.State);
    }

    [Fact]
    public void RunningUnhealthyMapsToNeedsAttention()
    {
        var result = TrayStateMapper.Map(Status(health: HealthState.RunningUnhealthy));

        Assert.Equal(TrayState.NeedsAttention, result.State);
    }

    [Fact]
    public void CrashedMapsToNeedsAttention()
    {
        var result = TrayStateMapper.Map(Status(health: HealthState.Crashed));

        Assert.Equal(TrayState.NeedsAttention, result.State);
    }

    [Fact]
    public void StartingHealthMapsToStarting()
    {
        var result = TrayStateMapper.Map(Status(health: HealthState.Starting));

        Assert.Equal(TrayState.Starting, result.State);
        Assert.Equal("Serpy: starting ERPNext", result.Tooltip);
    }

    [Fact]
    public void StartOperationMapsToStartingBeforeHealthChanges()
    {
        var result = TrayStateMapper.Map(Status(operation: OperationKind.Start));

        Assert.Equal(TrayState.Starting, result.State);
    }

    [Fact]
    public void RunningMapsToReadyAndStoppedMapsToStopped()
    {
        var ready = TrayStateMapper.Map(Status(health: HealthState.Running));
        var stopped = TrayStateMapper.Map(Status());

        Assert.Equal(TrayState.Ready, ready.State);
        Assert.Equal("Serpy: ERPNext is ready", ready.Tooltip);
        Assert.Equal(TrayState.Stopped, stopped.State);
        Assert.Equal("Serpy: ERPNext is stopped", stopped.Tooltip);
    }

    private static ApplianceStatus Status(
        HealthState health = HealthState.Stopped,
        OperationKind? operation = null,
        SetupSummary? setup = null) =>
        new(ReadinessState.Initialized, health, null, null, operation, null, Setup: setup);
}
