using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serpy.Core.Contracts;

namespace Serpy.App.ViewModels;

/// <summary>
/// U1 foundation: observable status state, tray-menu enablement, and shell commands.
/// Lifecycle operation dispatch (Start/Stop/Restart/Recover/Build/Init) is wired in U5/U6.
/// A ViewModel must never spawn QEMU, write state, or parse QMP.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    // ── Observable state ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanRestart), nameof(CanStop),
        nameof(IsHealthy), nameof(StatusBadgeText), nameof(PrimaryActionLabel))]
    private ApplianceStatus _status = new(
        ReadinessState.NotBuilt,
        HealthState.Stopped,
        LoopbackUrl: null,
        CurrentStage: null,
        ActiveOperation: null,
        ProgressPercent: null);

    [ObservableProperty] private string _stageText = "Not set up";
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private string _detailLog = string.Empty;

    // ── Derived properties (tray NativeMenu IsEnabled + dashboard badge) ──

    public bool CanStart =>
        Status.Readiness == ReadinessState.Initialized &&
        Status.ActiveOperation is null &&
        Status.Health is HealthState.Stopped or HealthState.Crashed;

    public bool CanRestart =>
        Status.Readiness == ReadinessState.Initialized &&
        Status.ActiveOperation is null &&
        Status.Health is HealthState.Running or HealthState.RunningUnhealthy;

    public bool CanStop =>
        Status.ActiveOperation is null &&
        Status.Health is HealthState.Running or HealthState.RunningUnhealthy;

    public bool IsHealthy => Status.Health == HealthState.Running;

    public string StatusBadgeText => Status.Health switch
    {
        HealthState.Running => "Running",
        HealthState.RunningUnhealthy => "Unhealthy",
        HealthState.Crashed => "Crashed",
        HealthState.Starting => "Starting…",
        _ => Status.Readiness switch
        {
            ReadinessState.NotBuilt => "Not set up",
            ReadinessState.Built => "Built (no data disk)",
            ReadinessState.Initialized => "Stopped",
            _ => "Unknown",
        },
    };

    /// <summary>
    /// Label for the next valid primary action — used by U6's first-run wizard.
    /// Read-only in U1; bound to an enabled button in U6.
    /// </summary>
    public string PrimaryActionLabel => Status.Readiness switch
    {
        ReadinessState.NotBuilt => "Build appliance",
        ReadinessState.Built => "Initialize",
        ReadinessState.Initialized when Status.Health == HealthState.Crashed => "Restart (clear crash)",
        ReadinessState.Initialized => "Start",
        _ => "Start",
    };

    // ── Shell commands (not lifecycle operations) ─────────────────────────

    /// <summary>Raise ShowDashboardRequested so App can show the window.</summary>
    [RelayCommand]
    private void ShowDashboard() => ShowDashboardRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Placeholder: tray menu binds to this; implementation added in U6.
    /// Always disabled in U1 (CanExecute = false).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() { /* wired in U5/U6 */ }

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart() { /* wired in U5/U6 */ }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() { /* wired in U5/U6 */ }

    [RelayCommand(CanExecute = nameof(IsHealthy))]
    private void OpenErpNext() { /* wired in U6 */ }

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    // ── Events (App layer handles platform calls) ─────────────────────────
    public event EventHandler? ShowDashboardRequested;
    public event EventHandler? ExitRequested;

    // ── Progress update (called by U5 operations layer) ───────────────────

    /// <summary>Apply a progress update from an in-flight operation. Called by U5.</summary>
    public void ApplyUpdate(OperationUpdate update)
    {
        StageText = update.Message;
        if (update.ProgressPercent.HasValue)
            ProgressPercent = update.ProgressPercent.Value;
        if (update.LogDetail is { } detail)
            DetailLog += detail + "\n";
    }
}
