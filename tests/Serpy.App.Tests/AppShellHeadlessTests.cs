using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Serpy.App.ViewModels;
using Serpy.App.Views;
using Serpy.Core.Contracts;

namespace Serpy.App.Tests;

public sealed class AppShellHeadlessTests
{
    [AvaloniaFact]
    public void Dashboard_RendersStatusAndOperationProgress()
    {
        var vm = new DashboardViewModel
        {
            Status = new ApplianceStatus(
                ReadinessState.Initialized, HealthState.Running,
                "http://127.0.0.1:18080", null, null, null),
        };
        vm.ApplyUpdate(new OperationUpdate(
            Guid.NewGuid(), OperationKind.Start, "health", UpdateSeverity.Info,
            "Checking ERPNext health", 75, null));
        var dashboard = new DashboardWindow { DataContext = vm };

        dashboard.Show();

        var text = dashboard.GetVisualDescendants().OfType<TextBlock>()
            .Select(block => block.Text).ToArray();
        var progress = Assert.Single(dashboard.GetVisualDescendants().OfType<ProgressBar>());
        Assert.Contains(vm.StatusBadgeText, text);
        Assert.Contains("Checking ERPNext health", text);
        Assert.Equal(75, progress.Value);

        dashboard.Close();
    }

    [AvaloniaFact]
    public void Application_DeclaresTrayMenuActions()
    {
        var app = new Serpy.App.App();
        app.Initialize();

        var icons = app.GetValue(TrayIcon.IconsProperty);
        var icon = Assert.Single(Assert.IsType<TrayIcons>(icons));
        var menu = Assert.IsType<NativeMenu>(icon.Menu);
        var headers = menu.Items.OfType<NativeMenuItem>().Select(item => item.Header).ToArray();

        Assert.Contains("Show Dashboard", headers);
        Assert.Contains("Start", headers);
        Assert.Contains("Restart", headers);
        Assert.Contains("Stop", headers);
        Assert.Contains("Open ERPNext", headers);
        Assert.Contains("Exit…", headers);
    }

    [AvaloniaFact]
    public void ApplicationDashboard_CloseHidesAndCanBeReopened()
    {
        var app = new Serpy.App.App();
        app.Initialize();
        app.ShowDashboardWindow();
        var dashboard = Assert.IsType<DashboardWindow>(app.DashboardWindow);

        dashboard.Close();

        Assert.False(dashboard.IsVisible);
        app.ShowDashboardWindow();
        Assert.Same(dashboard, app.DashboardWindow);
        Assert.True(dashboard.IsVisible);
    }

    [AvaloniaFact]
    public void TrayOnlyApplication_DoesNotCreateInitialDashboard()
    {
        var app = new Serpy.App.App(trayOnly: true);
        app.Initialize();

        Assert.True(app.IsTrayOnly);
        Assert.False(app.ShouldOpenDashboardInitially);
        Assert.Null(app.DashboardWindow);
    }

    [AvaloniaFact]
    public void InteractiveApplication_UsesExplicitShutdownMode()
    {
        Assert.Equal(
            ShutdownMode.OnExplicitShutdown,
            Serpy.App.App.RequiredShutdownMode);
    }


    [AvaloniaFact]
    public async Task RunningApplianceExit_StopChoiceStopsBeforeExit()
    {
        var service = new FakeApplianceService
        {
            Status = new ApplianceStatus(ReadinessState.Initialized, HealthState.Running, null, null, null, null),
        };
        var app = new Serpy.App.App { Service = service };
        app.Initialize();
        app.OnFrameworkInitializationCompleted();
        app.DashboardViewModel!.Status = service.Status;
        app.ExitChoiceRequested = () => Task.FromResult(ExitWhileRunningChoice.Stop);

        await app.HandleExitRequestAsync();

        Assert.Equal([OperationKind.Stop], service.CalledOperations);
    }

    [AvaloniaFact]
    public async Task RunningApplianceExit_LeaveChoiceDoesNotStop()
    {
        var service = new FakeApplianceService
        {
            Status = new ApplianceStatus(ReadinessState.Initialized, HealthState.Running, null, null, null, null),
        };
        var app = new Serpy.App.App { Service = service };
        app.Initialize();
        app.OnFrameworkInitializationCompleted();
        app.ExitChoiceRequested = () => Task.FromResult(ExitWhileRunningChoice.LeaveRunning);
        app.DashboardViewModel!.Status = service.Status;

        await app.HandleExitRequestAsync();

        Assert.Empty(service.CalledOperations);
    }
    [AvaloniaFact]
    public async Task TrayOnlyRunningApplianceExit_RequestsChoiceWithoutDashboard()
    {
        var service = new FakeApplianceService
        {
            Status = new ApplianceStatus(ReadinessState.Initialized, HealthState.Running, null, null, null, null),
        };
        var app = new Serpy.App.App(trayOnly: true) { Service = service };
        app.Initialize();
        app.OnFrameworkInitializationCompleted();
        app.DashboardViewModel!.Status = service.Status;
        app.ExitChoiceRequested = () => Task.FromResult(ExitWhileRunningChoice.LeaveRunning);

        await app.HandleExitRequestAsync();

        Assert.True(app.IsExiting);
        Assert.Null(app.DashboardWindow);
    }

    [AvaloniaFact]
    public void ExplicitExit_AllowsDashboardToCloseInsteadOfHiding()
    {
        var app = new Serpy.App.App();
        app.Initialize();
        app.ShowDashboardWindow();
        var dashboard = Assert.IsType<DashboardWindow>(app.DashboardWindow);

        app.DoExit();
        dashboard.Close();

        Assert.True(app.IsExiting);
        Assert.False(dashboard.IsVisible);
    }

    [AvaloniaFact]
    public async Task RunningApplianceExit_CancelChoiceKeepsApplicationAlive()
    {
        var service = new FakeApplianceService
        {
            Status = new ApplianceStatus(ReadinessState.Initialized, HealthState.Running, null, null, null, null),
        };
        var app = new Serpy.App.App { Service = service };
        app.Initialize();
        app.OnFrameworkInitializationCompleted();
        app.DashboardViewModel!.Status = service.Status;
        app.ExitChoiceRequested = () => Task.FromResult(ExitWhileRunningChoice.Cancel);

        await app.HandleExitRequestAsync();

        Assert.False(app.IsExiting);
        Assert.Empty(service.CalledOperations);
    }

}
