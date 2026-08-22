using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serpy.Core.Contracts;
using Serpy.App.Platform;
using Serpy.App.ViewModels;
using Serpy.App.Platform.Windows;
using Serpy.App.Views;

namespace Serpy.App;

public sealed class App : Application
{
    private readonly bool _trayOnly;
    private DashboardViewModel? _dashboardVm;
    private DashboardWindow? _dashboardWindow;
    private SplashWindow? _splashWindow;
    private SplashViewModel? _splashVm;
    private readonly BrowserLauncher _browser = new();
    private Avalonia.Threading.DispatcherTimer? _pollTimer;
    private bool _splashOpenedForThisStart;
    private bool _isExiting;

    // Injected by the composition root after construction.
    public IApplianceService? Service { get; set; }

    public App() : this(trayOnly: false) { }
    public App(bool trayOnly) { _trayOnly = trayOnly; }

    internal DashboardWindow? DashboardWindow => _dashboardWindow;
    internal DashboardViewModel? DashboardViewModel => _dashboardVm;
    internal bool IsTrayOnly => _trayOnly;
    internal bool ShouldOpenDashboardInitially => !_trayOnly;
    internal bool IsExiting => _isExiting;
    internal static ShutdownMode RequiredShutdownMode => ShutdownMode.OnExplicitShutdown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _dashboardVm = new DashboardViewModel();
        DataContext = _dashboardVm;

        // Wire events from ViewModel to App platform handlers.
        _dashboardVm.ShowDashboardRequested += (_, _) => ShowDashboardWindow();
        _dashboardVm.ExitRequested         += (_, _) => HandleExitRequest();
        _dashboardVm.OpenUrlRequested      += (_, url) => _browser.OpenOnce(url);
        _dashboardVm.CredentialInputRequested = RequestCredentialsAsync;
        _dashboardVm.RecoverInputRequested = RequestRecoveryImageAsync;
        if (Service is not null)
            _dashboardVm.SetService(Service);
        _dashboardVm.OperationUpdated += (_, update) => _splashVm?.ApplyUpdate(update);
        if (OperatingSystem.IsWindows())
        {
            _dashboardVm.AutostartRequested += (_, enabled) => HandleAutostartRequest(enabled);
            _dashboardVm.StartAtSignIn = RunKeyAutostart.IsEnabled();
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = RequiredShutdownMode;
            desktop.Exit += (_, _) => _pollTimer?.Stop();

            // Start status polling (sync tick, async refresh fire-and-forget).
            var timer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3),
            };
            timer.Tick += (_, _) =>
            {
                // Fire and forget — poll without blocking the UI thread.
                _dashboardVm.RefreshStatusAsync()
                    .ContinueWith(_ => HandlePostStatusUpdate(),
                        TaskScheduler.FromCurrentSynchronizationContext());
            };
            timer.Start();
            _pollTimer = timer;

            if (ShouldOpenDashboardInitially)
                ShowDashboardWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ── Status-driven side-effects ────────────────────────────────────────

    private void HandlePostStatusUpdate()
    {
        if (_dashboardVm is null) return;
        var status = _dashboardVm.Status;

        // Auto-open browser once when appliance reaches healthy (AE8, R17).
        if (status.Health == HealthState.Running && status.LoopbackUrl is { } url)
        {
            _browser.OpenOnce(url);
            DismissSplash();
        }
        else if (status.ActiveOperation is null && _splashOpenedForThisStart)
        {
            // Terminal state (stopped/crashed/unhealthy) — dismiss splash if shown.
            DismissSplash();
        }

        if (status.Health == HealthState.Starting && !_splashOpenedForThisStart)
        {
            _browser.ResetForNewStart();
            ShowSplash();
            _splashOpenedForThisStart = true;
        }
    }

    // ── Splash ────────────────────────────────────────────────────────────

    private void ShowSplash()
    {
        if (_splashWindow is not null) return;
        _splashVm = new SplashViewModel();
        _splashWindow = new SplashWindow { DataContext = _splashVm };
        _splashWindow.Show();
    }

    private void DismissSplash()
    {
        _splashWindow?.Close();
        _splashWindow = null;
        _splashVm = null;
        _splashOpenedForThisStart = false;
    }

    // ── Dashboard ─────────────────────────────────────────────────────────

    internal void ShowDashboardWindow()
    {
        if (_dashboardWindow is not null)
        {
            _dashboardWindow.Show();
            _dashboardWindow.Activate();
            return;
        }

        _dashboardWindow = new DashboardWindow { DataContext = _dashboardVm };
        _dashboardWindow.Closing += (_, e) =>
        {
            if (_isExiting) return;
            e.Cancel = true;
            _dashboardWindow.Hide(); // Close → hide; tray remains.
        };
        _dashboardWindow.Show();
    }

    // ── Exit guard ────────────────────────────────────────────────────────

    internal async Task HandleExitRequestAsync()
    {
        var status = _dashboardVm?.Status;
        if (status?.Health is not (HealthState.Running or HealthState.RunningUnhealthy))
        {
            DoExit();
            return;
        }

        var choice = ExitChoiceRequested is not null
            ? await ExitChoiceRequested()
            : await RequestExitChoiceAsync();
        if (choice == ExitWhileRunningChoice.Stop)
        {
            await _dashboardVm!.StopCommand.ExecuteAsync(null);
            if (_dashboardVm.Status.Health is HealthState.Running or HealthState.RunningUnhealthy)
                return;
            DoExit();
        }
        else if (choice == ExitWhileRunningChoice.LeaveRunning)
        {
            DoExit();
        }
    }

    private void HandleExitRequest() => _ = HandleExitRequestAsync();

    internal Func<Task<ExitWhileRunningChoice>>? ExitChoiceRequested { get; set; }

    public void DoExit()
    {
        _isExiting = true;
        _pollTimer?.Stop();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // ── Credential dialog ─────────────────────────────────────────────────

    private async Task<ExitWhileRunningChoice> RequestExitChoiceAsync()
    {
        if (_dashboardWindow is null) ShowDashboardWindow();
        if (_dashboardWindow is null) return ExitWhileRunningChoice.Cancel;
        return await new ExitWhileRunningDialog()
            .ShowDialog<ExitWhileRunningChoice>(_dashboardWindow);
    }

    private async Task<InitializationParameters?> RequestCredentialsAsync()
    {
        // Show the initialization credential dialog on the UI thread.
        if (_dashboardWindow is null) ShowDashboardWindow();
        if (_dashboardWindow is null) return null; // headless/test lifetime
        var dialog = new InitializeDialog();
        return await dialog.ShowDialog<InitializationParameters?>(_dashboardWindow);
    }

    private async Task<string?> RequestRecoveryImageAsync()
    {
        if (_dashboardWindow is null) ShowDashboardWindow();
        if (_dashboardWindow?.StorageProvider is not { CanOpen: true } storage) return null;

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select Serpy replacement system image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("QEMU copy-on-write image")
                {
                    Patterns = ["*.qcow2"],
                },
            ],
        });
        if (files.Count != 1 || !files[0].Path.IsFile) return null;
        var imagePath = files[0].Path.LocalPath;
        var confirmation = new RecoveryConfirmationDialog(imagePath);
        return await confirmation.ShowDialog<bool>(_dashboardWindow) ? imagePath : null;
    }

    private void HandleAutostartRequest(bool enabled)
    {
        if (OperatingSystem.IsWindows())
            SetAutostart(enabled);
    }

    [SupportedOSPlatform("windows")]
    private void SetAutostart(bool enabled)
    {
        if (_dashboardVm is null) return;
        try
        {
            if (enabled)
                RunKeyAutostart.Enable(Path.Combine(AppContext.BaseDirectory, "Serpy.App.exe"));
            else
                RunKeyAutostart.Disable();
            _dashboardVm.StartAtSignIn = enabled;
        }
        catch (AutostartException ex)
        {
            _dashboardVm.StartAtSignIn = false;
            _dashboardVm.StageText = ex.Message;
        }
    }
}
