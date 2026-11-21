using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serpy.App.Text;
using Serpy.Core.Contracts;
using Serpy.App.Platform;
using Serpy.App.ViewModels;
using Serpy.App.Platform.Windows;
using Serpy.App.Views;
using Serpy.App.Tray;
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
    private bool _initialAutoStartChecked;
    private readonly NotificationPolicy _notificationPolicy = new();
    private readonly ToastNotifier _toastNotifier = new();
    private TrayState _previousTrayState = TrayState.Stopped;

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

        if (Service is not null)
        {
            _dashboardVm.SetService(Service);
            _dashboardVm.SetSetupService(Service);
        }
        WireSetupEvents(_dashboardVm);
        _dashboardVm.OperationUpdated += (_, update) => _splashVm?.ApplyUpdate(update);

        // Wire events from ViewModel to App platform handlers.
        _dashboardVm.ShowDashboardRequested += (_, _) => ShowDashboardWindow();
        _dashboardVm.ExitRequested         += (_, _) => HandleExitRequest();
        _dashboardVm.OpenUrlRequested      += (_, url) => _browser.OpenOnce(url);
        _dashboardVm.SetupInputRequested           = RequestSetupChoiceAsync;
        _dashboardVm.CredentialInputRequested      = RequestCredentialsAsync;
        _dashboardVm.RecoverInputRequested         = RequestRecoveryImageAsync;
        _dashboardVm.LegacyConsentInputRequested   = RequestLegacyConsentAsync;
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
            _ = _dashboardVm.RefreshStatusAsync()
                .ContinueWith(_ => HandlePostStatusUpdate(),
                    TaskScheduler.FromCurrentSynchronizationContext());
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

        UpdateTrayState(status);

        // Guided setup owns the window while it is active: no auto-browser (AE13), no splash.
        if (_dashboardVm.Setup.SetupActive)
        {
            DismissSplash();
            return;
        }

        // Automatic start of ERPNext on launch when already set up (KTD18, R33).
        if (!_initialAutoStartChecked)
        {
            _initialAutoStartChecked = true;
            if (status.RecommendedRoute == LaunchRoute.Start &&
                status.Health == HealthState.Stopped &&
                status.ActiveOperation is null)
            {
                _ = _dashboardVm.StartCommand.ExecuteAsync(null);
            }
        }

        // Terminal state (stopped/crashed/unhealthy) or healthy — dismiss splash if shown.
        if (status.Health == HealthState.Running || (status.ActiveOperation is null && _splashOpenedForThisStart))
        {
            DismissSplash();
        }

        if (status.Health == HealthState.Starting && !_splashOpenedForThisStart && !_trayOnly)
        {
            ShowSplash();
            _splashOpenedForThisStart = true;
        }
    }

    private void UpdateTrayState(ApplianceStatus status)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is null || icons.Count == 0) return;
        var trayIcon = icons[0];

        SetupStepId? currentStep = _dashboardVm?.Setup is { SetupActive: true } setup
            ? GetCurrentSetupStep(setup)
            : null;

        var mapped = TrayStateMapper.Map(status, currentStep);
        trayIcon.ToolTipText = mapped.Tooltip;

        var notification = _notificationPolicy.Evaluate(_previousTrayState, mapped.State, status);
        _previousTrayState = mapped.State;
        if (notification is not null)
        {
            _toastNotifier.Show(notification.Title, notification.Message);
        }
        try
        {
            var iconUri = new Uri($"avares://Serpy.App{TrayStateMapper.IconPath(mapped.State)}");
            if (Avalonia.Platform.AssetLoader.Exists(iconUri))
            {
                trayIcon.Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(iconUri));
            }
        }
        catch
        {
            // Keep existing icon if asset load fails
        }
    }

    private static SetupStepId? GetCurrentSetupStep(SetupViewModel setup)
    {
        if (setup.ShowChecking) return SetupStepId.PcCheck;
        if (setup.ShowDownloads) return SetupStepId.Download;
        if (setup.ShowInstalling) return SetupStepId.Build;
        return null;
    }
    // ── Splash ────────────────────────────────────────────────────────────

    private void ShowSplash()
    {
        if (_splashWindow is not null) return;
        _splashVm = new SplashViewModel();
        _splashWindow = new SplashWindow { DataContext = _splashVm };
        _splashWindow.Show();
    }


    // ── Guided setup (U9) ─────────────────────────────────────────────────

    private void WireSetupEvents(DashboardViewModel vm)
    {
        var setup = vm.Setup;
        vm.SetupActiveChanged += (_, active) =>
        {
            if (active) ShowDashboardWindow();
        };
        vm.ShowSetupRequested += (_, _) => setup.Activate();
        setup.StartSetupRequested += (_, _) => ShowDashboardWindow();
        setup.SetupStarted += (_, _) => ShowDashboardWindow();
        setup.OpenUrlRequested += (_, url) => _browser.OpenOnce(url);
        setup.SaveDetailsRequested += (_, content) => _ = SaveSetupDetailsAsync(content);
        // RestartNowRequested / RestartLaterRequested are completed by U7 (elevation + restart flow);
        // U9 only raises them.
    }

    private async Task SaveSetupDetailsAsync(string content)
    {
        if (_dashboardWindow is null) ShowDashboardWindow();
        if (_dashboardWindow?.StorageProvider is not { CanSave: true } storage) return;
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = UiText.SaveDetails,
            SuggestedFileName = "serpy-setup-details.txt",
            DefaultExtension = "txt",
        });
        if (file is null) return;
        await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, content);
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
        var isSetupWorking = _dashboardVm?.Setup is { SetupActive: true, ShowStepper: true };
        var isApplianceRunning = _dashboardVm?.Status.Health is HealthState.Running or HealthState.RunningUnhealthy;

        if (!isSetupWorking && !isApplianceRunning)
        {
            DoExit();
            return;
        }

        var choice = ExitChoiceRequested is not null
            ? await ExitChoiceRequested()
            : await RequestExitChoiceAsync(isSetupWorking);

        if (choice == ExitWhileRunningChoice.Stop)
        {
            if (isSetupWorking)
            {
                _dashboardVm!.Setup.CancelSetupCommand.Execute(null);
            }
            else
            {
                await _dashboardVm!.StopCommand.ExecuteAsync(null);
                if (_dashboardVm.Status.Health is HealthState.Running or HealthState.RunningUnhealthy)
                    return;
            }
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

    private async Task<ExitWhileRunningChoice> RequestExitChoiceAsync(bool isSetup = false)
    {
        if (_dashboardWindow is null) ShowDashboardWindow();
        if (_dashboardWindow is null) return ExitWhileRunningChoice.Cancel;
        return await new ExitWhileRunningDialog(isSetup)
            .ShowDialog<ExitWhileRunningChoice>(_dashboardWindow);
    }

    private async Task<SetupChoice?> RequestSetupChoiceAsync()
    {
        if (_dashboardWindow is null) ShowDashboardWindow();
        if (_dashboardWindow is null) return null; // headless/test lifetime
        bool hasData = _dashboardVm?.Status.HasCommittedDataOnDisk ?? false;
        var dialog = new InitializeDialog(hasCommittedDataOnDisk: hasData);
        return await dialog.ShowDialog<SetupChoice?>(_dashboardWindow);
    }
    private async Task<InitializationParameters?> RequestCredentialsAsync()
    {
        // Show the initialization credential dialog on the UI thread.
        if (_dashboardWindow is null) ShowDashboardWindow();
        if (_dashboardWindow is null) return null; // headless/test lifetime
        var dialog = new InitializeDialog(allowChoice: false);
        var result = await dialog.ShowDialog<SetupChoice?>(_dashboardWindow);
        return result is SetupChoice.CreateNew createNew ? createNew.Parameters : null;
    }

    private async Task<bool> RequestLegacyConsentAsync(ArchiveInspection inspection)
    {
        if (_dashboardWindow is null) ShowDashboardWindow();
        if (_dashboardWindow is null) return false;
        var dialog = new RecoveryConfirmationDialog(inspection.CanonicalPath);
        return await dialog.ShowDialog<bool>(_dashboardWindow);
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
