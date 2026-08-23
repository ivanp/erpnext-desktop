using System.Runtime.Versioning;
using System.Security.AccessControl;
using Serpy.App.Platform.Windows;
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

    [Fact]
    public void BuildAndInitializeCommands_FollowReadinessState()
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.NotBuilt, HealthState.Stopped);
        Assert.True(vm.CanBuild);
        Assert.False(vm.CanInitialize);

        vm.Status = Status(ReadinessState.Built, HealthState.Stopped);
        Assert.False(vm.CanBuild);
        Assert.True(vm.CanInitialize);
    }
    [Theory]
    [InlineData(HealthState.Stopped, true)]
    [InlineData(HealthState.Running, false)]
    [InlineData(HealthState.Crashed, false)]
    public void CanRecover_RequiresInitializedStopped(HealthState health, bool expected)
    {
        var vm = MakeVm();
        vm.Status = Status(ReadinessState.Initialized, health);

        Assert.Equal(expected, vm.CanRecover);
    }

    [Fact]
    public async Task RecoverCommand_DoesNotDispatchWhenSelectionCancelled()
    {
        var service = new FakeApplianceService { Status = Status(ReadinessState.Initialized, HealthState.Stopped) };
        var vm = MakeVm();
        vm.RecoverInputRequested = () => Task.FromResult<string?>(null);
        vm.SetService(service);
        vm.Status = service.Status;

        await vm.RecoverCommand.ExecuteAsync(null);

        Assert.Empty(service.CalledOperations);
        Assert.Equal("Recovery cancelled — no replacement image selected.", vm.StageText);
    }

    [Fact]
    public void SetAutostartCommand_RaisesRequestedState()
    {
        var vm = MakeVm();
        bool? requested = null;
        vm.AutostartRequested += (_, enabled) => requested = enabled;

        vm.SetAutostartCommand.Execute(true);

        Assert.True(requested);
    }

    [Fact]
    public void SetAutostartCommand_NullInputRequestsDisabled()
    {
        var vm = MakeVm();
        bool? requested = true;
        vm.AutostartRequested += (_, enabled) => requested = enabled;

        vm.SetAutostartCommand.Execute(null);

        Assert.False(requested);
    }

    [Fact]
    public void ApplyUpdate_ForwardsSameProgressToSplashSubscriber()
    {
        var vm = MakeVm();
        var splash = new SplashViewModel();
        vm.OperationUpdated += (_, update) => splash.ApplyUpdate(update);

        vm.ApplyUpdate(new OperationUpdate(
            Guid.NewGuid(), OperationKind.Start, "Starting", UpdateSeverity.Info,
            "Starting services", 42, null));

        Assert.Equal("Starting services", splash.StageText);
        Assert.Equal(42, splash.ProgressPercent);
        Assert.False(splash.IsIndeterminate);
    }

    [Fact]
    public async Task RecoverCommand_UsesConfirmedReplacementPath()
    {
        var service = new FakeApplianceService { Status = Status(ReadinessState.Initialized, HealthState.Stopped) };
        var vm = MakeVm();
        vm.RecoverInputRequested = () => Task.FromResult<string?>("C:\\images\\replacement.qcow2");
        vm.SetService(service);
        vm.Status = service.Status;

        await vm.RecoverCommand.ExecuteAsync(null);

        Assert.Equal([OperationKind.Recover], service.CalledOperations);
        Assert.Equal("C:\\images\\replacement.qcow2", service.RecoveryImagePath);
    }

    [SupportedOSPlatform("windows")]
    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void WriteCapableAceBits_AreRejectedByAutostartGuard(FileSystemRights rights)
    {
        Assert.True(RunKeyAutostart.IsWriteCapable(rights));
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

    [Theory]
    [InlineData(ReadinessState.NotBuilt, HealthState.Stopped, OperationKind.Build)]
    [InlineData(ReadinessState.Initialized, HealthState.Stopped, OperationKind.Start)]
    [InlineData(ReadinessState.Initialized, HealthState.Crashed, OperationKind.Restart)]
    public async Task PrimaryAction_DispatchesOperationForCurrentState(
        ReadinessState readiness, HealthState health, OperationKind expected)
    {
        var service = new FakeApplianceService { Status = Status(readiness, health) };
        var vm = MakeVm();
        vm.SetService(service);
        vm.Status = service.Status;

        await vm.PrimaryActionCommand.ExecuteAsync(null);

        Assert.Equal([expected], service.CalledOperations);
    }
}
