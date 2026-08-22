using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serpy.Core.Contracts;

namespace Serpy.App.ViewModels;

/// <summary>
/// Drives the dashboard view and the tray icon NativeMenu (U6).
/// All lifecycle operations dispatch through IApplianceService.
/// Lifecycle rules live in the service; this VM only maps status → UI state.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private IApplianceService? _service;
    private CancellationTokenSource? _operationCts;

    // ── Observable state ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild), nameof(CanInitialize), nameof(CanRecover),
        nameof(CanStart), nameof(CanRestart), nameof(CanStop), nameof(IsHealthy),
        nameof(StatusBadgeText), nameof(PrimaryActionLabel), nameof(CanExecutePrimaryAction))]
    private ApplianceStatus _status = new(
        ReadinessState.NotBuilt, HealthState.Stopped,
        null, null, null, null);

    [ObservableProperty] private string _stageText = "Not set up";
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private bool _startAtSignIn;
    [ObservableProperty] private string _detailLog = string.Empty;
    [ObservableProperty] private bool _isOperationRunning;

    // ── Derived properties (tray + dashboard) ─────────────────────────────

    public bool CanBuild =>
        Status.Readiness == ReadinessState.NotBuilt &&
        Status.ActiveOperation is null &&
        !IsOperationRunning;

    public bool CanInitialize =>
        Status.Readiness == ReadinessState.Built &&
        Status.ActiveOperation is null &&
        !IsOperationRunning;
    public bool CanRecover =>
        Status.Readiness == ReadinessState.Initialized &&
        Status.Health == HealthState.Stopped &&
        Status.ActiveOperation is null &&
        !IsOperationRunning;


    public bool CanStart =>
        Status.Readiness == ReadinessState.Initialized &&
        Status.ActiveOperation is null &&
        !IsOperationRunning &&
        Status.Health is HealthState.Stopped or HealthState.Crashed;

    public bool CanRestart =>
        Status.Readiness == ReadinessState.Initialized &&
        Status.ActiveOperation is null &&
        !IsOperationRunning &&
        Status.Health is HealthState.Running or HealthState.RunningUnhealthy;

    public bool CanStop =>
        Status.ActiveOperation is null &&
        !IsOperationRunning &&
        Status.Health is HealthState.Running or HealthState.RunningUnhealthy;

    public bool IsHealthy => Status.Health == HealthState.Running;

    public string StatusBadgeText => Status.Health switch
    {
        HealthState.Running          => "Running",
        HealthState.RunningUnhealthy => "Unhealthy",
        HealthState.Crashed          => "Crashed",
        HealthState.Starting         => "Starting…",
        _ => Status.Readiness switch
        {
            ReadinessState.NotBuilt     => "Not set up",
            ReadinessState.Built        => "Built (no data disk)",
            ReadinessState.Initialized  => "Stopped",
            _                           => "Unknown",
        },
    };

    public string PrimaryActionLabel => Status.Readiness switch
    {
        ReadinessState.NotBuilt    => "Build appliance",
        ReadinessState.Built       => "Initialize",
        ReadinessState.Initialized when Status.Health == HealthState.Crashed
                                   => "Restart (clear crash)",
        ReadinessState.Initialized => "Start",
        _                          => "Start",
    };

    public bool CanExecutePrimaryAction =>
        CanBuild || CanInitialize || CanStart || CanRestart;

    // ── Service injection ─────────────────────────────────────────────────

    public void SetService(IApplianceService service) => _service = service;

    // ── Status polling ────────────────────────────────────────────────────

    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        if (_service is null) return;
        var s = await _service.GetStatusAsync(ct);
        Status = s;
        if (s.CurrentStage is { } stage) StageText = stage;
        if (s.ProgressPercent.HasValue) ProgressPercent = s.ProgressPercent.Value;
    }

    // ── Shell commands (no service call) ─────────────────────────────────

    [RelayCommand]
    private void ShowDashboard() => ShowDashboardRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void SetAutostart(bool? enabled) => AutostartRequested?.Invoke(this, enabled == true);

    [RelayCommand(CanExecute = nameof(IsHealthy))]
    private void OpenErpNext()
    {
        if (Status.LoopbackUrl is { } url)
            OpenUrlRequested?.Invoke(this, url);
    }

    // ── Lifecycle commands (dispatch to service) ──────────────────────────

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => DispatchAsync(OperationKind.Start);

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => DispatchAsync(OperationKind.Restart);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => DispatchAsync(OperationKind.Stop);

    [RelayCommand(CanExecute = nameof(CanExecutePrimaryAction))]
    private Task PrimaryActionAsync() => Status.Readiness switch
    {
        ReadinessState.NotBuilt => DispatchAsync(OperationKind.Build),
        ReadinessState.Built => DispatchInitializeAsync(),
        ReadinessState.Initialized when Status.Health == HealthState.Crashed =>
            DispatchAsync(OperationKind.Restart),
        ReadinessState.Initialized => DispatchAsync(OperationKind.Start),
        _ => Task.CompletedTask,
    };

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private Task BuildAsync() => DispatchAsync(OperationKind.Build);

    [RelayCommand(CanExecute = nameof(CanInitialize))]
    private Task InitializeAsync() => DispatchInitializeAsync();

    [RelayCommand(CanExecute = nameof(CanRecover))]
    private Task RecoverAsync() => DispatchRecoverAsync();

    [RelayCommand]
    private void Cancel()
    {
        _operationCts?.Cancel();
        StageText = "Cancelling…";
    }

    // ── Progress / log update ─────────────────────────────────────────────

    public void ApplyUpdate(OperationUpdate update)
    {
        StageText = update.Message;
        if (update.ProgressPercent.HasValue)
            ProgressPercent = update.ProgressPercent.Value;
        if (update.LogDetail is { } detail)
            DetailLog += detail + "\n";
        OperationUpdated?.Invoke(this, update);
    }

    public event EventHandler<OperationUpdate>? OperationUpdated;
    // ── Events ────────────────────────────────────────────────────────────

    public event EventHandler? ShowDashboardRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler<string>? OpenUrlRequested;
    public event EventHandler<bool>? AutostartRequested;

    /// <summary>Assigned by the App layer to collect site name + admin password from the user.</summary>
    public Func<Task<InitializationParameters?>>? CredentialInputRequested { get; set; }
    public Func<Task<string?>>? RecoverInputRequested { get; set; }

    // ── Internals ─────────────────────────────────────────────────────────

    private async Task DispatchAsync(OperationKind kind)
    {
        if (_service is null) return;
        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        DetailLog = string.Empty;

        try
        {
            var progress = new Progress<OperationUpdate>(ApplyUpdate);
            OperationResult result = kind switch
            {
                OperationKind.Start   => await _service.StartAsync(progress, _operationCts.Token),
                OperationKind.Stop    => await _service.StopAsync(progress, _operationCts.Token),
                OperationKind.Restart => await _service.RestartAsync(progress, _operationCts.Token),
                OperationKind.Build   => await _service.BuildAsync(progress, _operationCts.Token),
                _                     => throw new InvalidOperationException($"Unhandled: {kind}"),
            };

            StageText = result.Message;
            if (result.LogPath is { } lp) DetailLog += $"\nLog: {lp}";
        }
        catch (OperationCanceledException)
        {
            StageText = "Operation cancelled.";
        }
        finally
        {
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
            await RefreshStatusAsync();
        }
    }

    private async Task DispatchInitializeAsync()
    {
        if (_service is null) return;

        var parameters = CredentialInputRequested is null
            ? null
            : await CredentialInputRequested.Invoke();
        if (parameters is null)
        {
            StageText = "Initialization cancelled — no credentials provided.";
            return;
        }

        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        DetailLog = string.Empty;
        try
        {
            var result = await _service.InitializeAsync(
                parameters, new Progress<OperationUpdate>(ApplyUpdate), _operationCts.Token);
            StageText = result.Message;
        }
        catch (OperationCanceledException)
        {
            StageText = "Initialization cancelled.";
        }
        finally
        {
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
            await RefreshStatusAsync();
        }
    }

    private async Task DispatchRecoverAsync()
    {
        if (_service is null || RecoverInputRequested is null) return;
        var path = await RecoverInputRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            StageText = "Recovery cancelled — no replacement image selected.";
            return;
        }

        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        DetailLog = string.Empty;
        try
        {
            var result = await _service.RecoverAsync(
                path, new Progress<OperationUpdate>(ApplyUpdate), _operationCts.Token);
            StageText = result.Message;
            if (result.LogPath is { } lp) DetailLog += $"\nLog: {lp}";
        }
        catch (OperationCanceledException)
        {
            StageText = "Recovery cancelled.";
        }
        finally
        {
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
            await RefreshStatusAsync();
        }
    }
}
