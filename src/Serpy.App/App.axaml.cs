using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Serpy.App.ViewModels;
using Serpy.App.Views;

namespace Serpy.App;

public sealed class App : Application
{
    private readonly bool _trayOnly;
    private DashboardViewModel? _dashboardVm;
    private DashboardWindow? _dashboardWindow;

    public App() : this(trayOnly: false) { }

    public App(bool trayOnly)
    {
        _trayOnly = trayOnly;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _dashboardVm = new DashboardViewModel();

        // Wire tray menu to the same view model.
        DataContext = _dashboardVm;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (!_trayOnly)
            {
                ShowDashboardWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    internal void ShowDashboardWindow()
    {
        if (_dashboardWindow is { IsVisible: true })
        {
            _dashboardWindow.Activate();
            return;
        }

        _dashboardWindow = new DashboardWindow { DataContext = _dashboardVm };
        // Closing hides the window rather than disposing the application.
        _dashboardWindow.Closing += (_, e) =>
        {
            e.Cancel = true;
            _dashboardWindow.Hide();
        };
        _dashboardWindow.Show();
    }
}
