using Serpy.App.ViewModels;
using Serpy.Core.Contracts;

namespace Serpy.App.Tests.ViewModels;

/// <summary>
/// U1 test scenarios: status-driven command enablement, badge/label derivation, Show Details default.
/// Service dispatch tests are in U5/U6 (when IApplianceService is wired).
/// </summary>
public sealed class DashboardViewModelTests
{
    private static DashboardViewModel MakeVm() => new();

    private static ApplianceStatus Status(
        ReadinessState r = ReadinessState.Initialized,
        HealthState h = HealthState.Stopped,
        OperationKind? op = null) =>
        new(r, h, null, null, op, null);

    // ── CanStart ────────────────────────────────────────────────────────

    [Fact]
    public void CanStart_InitializedStopped_True()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, HealthState.Stopped);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public void CanStart_InitializedCrashed_True()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, HealthState.Crashed);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public void CanStart_Running_False()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, HealthState.Running);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void CanStart_NotBuilt_False()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.NotBuilt, HealthState.Stopped);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void CanStart_ActiveOperation_False()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, HealthState.Stopped, op: OperationKind.Build);
        Assert.False(vm.CanStart);
    }

    // ── CanRestart ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(HealthState.Running)]
    [InlineData(HealthState.RunningUnhealthy)]
    public void CanRestart_WhenRunning_True(HealthState h)
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, h);
        Assert.True(vm.CanRestart);
    }

    [Theory]
    [InlineData(HealthState.Stopped)]
    [InlineData(HealthState.Crashed)]
    public void CanRestart_WhenNotRunning_False(HealthState h)
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, h);
        Assert.False(vm.CanRestart);
    }

    // ── CanStop ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HealthState.Running)]
    [InlineData(HealthState.RunningUnhealthy)]
    public void CanStop_WhenRunning_True(HealthState h)
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, h);
        Assert.True(vm.CanStop);
    }

    [Fact]
    public void CanStop_WhenStopped_False()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, HealthState.Stopped);
        Assert.False(vm.CanStop);
    }

    // ── IsHealthy ───────────────────────────────────────────────────────

    [Fact]
    public void IsHealthy_Running_True()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, HealthState.Running);
        Assert.True(vm.IsHealthy);
    }

    [Fact]
    public void IsHealthy_RunningUnhealthy_False()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, HealthState.RunningUnhealthy);
        Assert.False(vm.IsHealthy);
    }

    // ── PrimaryActionLabel ───────────────────────────────────────────────

    [Theory]
    [InlineData(ReadinessState.NotBuilt, HealthState.Stopped, "Build appliance")]
    [InlineData(ReadinessState.Built, HealthState.Stopped, "Initialize")]
    [InlineData(ReadinessState.Initialized, HealthState.Stopped, "Start")]
    [InlineData(ReadinessState.Initialized, HealthState.Crashed, "Restart (clear crash)")]
    public void PrimaryActionLabel_MapsReadinessPlusHealth(
        ReadinessState r, HealthState h, string expected)
    {
        var vm = MakeVm();
        vm.Status = Status(r, h);
        Assert.Equal(expected, vm.PrimaryActionLabel);
    }

    // ── ShowDetails default ──────────────────────────────────────────────

    [Fact]
    public void ShowDetails_DefaultFalse()
    {
        var vm = MakeVm();
        Assert.False(vm.ShowDetails);
    }
}
