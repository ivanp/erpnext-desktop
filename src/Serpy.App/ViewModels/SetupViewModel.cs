using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serpy.App.Text;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Operations;
using Serpy.Core.Setup;

namespace Serpy.App.ViewModels;

/// <summary>Which mockup screen the setup view is currently showing.</summary>
public enum SetupScreen
{
    Welcome,
    Checking,
    CannotRun,
    RestartNeeded,
    PasswordNeeded,
    Downloads,
    Installing,
    Ready,
    Problem,
}

/// <summary>One row of the progress strip shown during setup.</summary>
public sealed class SetupStripItem : ObservableObject
{
    private SetupStripState _state = SetupStripState.Pending;

    public SetupStripItem(int position, string label)
    {
        Position = position;
        Label = label;
        AutomationId = $"StripItem{position}";
    }


    public int Position { get; }

    public string Label { get; }

    public string AutomationId { get; }

    public string StateText => State switch
    {
        SetupStripState.Working => "…",
        SetupStripState.Done => "✓",
        _ => string.Empty,
    };

    public SetupStripState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }
}

public enum SetupStripState
{
    Pending,
    Working,
    Done,
}

/// <summary>
/// Presentation-only view model for the guided setup screens. Maps
/// <see cref="SetupUpdate"/> and <see cref="ApplianceStatus"/> reports to screen
/// state and forwards user commands to <see cref="IApplianceService"/>; it makes
/// no readiness, routing or file decisions of its own.
/// </summary>
public partial class SetupViewModel : ObservableObject
{
    private const string DetailsPlaceholder = "Log details appear here once setup starts.";

    private IApplianceService? _service;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private CancellationTokenSource? _runCts;
    private int _runInFlight;
    private Guid _currentRunId;
    private LogTailReader? _tailReader;
    private readonly StringBuilder _detailsBuffer = new();
    private bool _passwordSubmitInFlight;
    private SetupScreen _previousScreenBeforePassword;

    public SetupViewModel(IApplianceService? service)
    {
        _service = service;
        StripItems =
        [
            new SetupStripItem(1, UiText.StripLabel(1)),
            new SetupStripItem(2, UiText.StripLabel(2)),
            new SetupStripItem(3, UiText.StripLabel(3)),
            new SetupStripItem(4, UiText.StripLabel(4)),
            new SetupStripItem(5, UiText.StripLabel(5)),
        ];
    }

    #region Events (App / DashboardViewModel wiring)

    /// <summary>Raised once per Start setup click; App shows the window.</summary>
    public event EventHandler? StartSetupRequested;

    /// <summary>Raised after RunSetupAsync has been dispatched; App shows the window.</summary>
    public event EventHandler? SetupStarted;

    /// <summary>User asked to keep waiting while setup is stuck.</summary>
    public event EventHandler? KeepWaitingRequested;

    /// <summary>User asked to retry the stuck step; service call already made.</summary>
    public event EventHandler? TryAgainStepRequested;

    /// <summary>User cancelled an active run (VM token only).</summary>
    public event EventHandler? CancelRequested;

    /// <summary>Open a URL (ERPNext) in the browser; carries the absolute URL.</summary>
    public event EventHandler<string>? OpenUrlRequested;

    /// <summary>Save composed setup details to a file; carries the full text.</summary>
    public event EventHandler<string>? SaveDetailsRequested;

    /// <summary>User chose to restart now to finish setup (U7 wiring).</summary>
    public event EventHandler? RestartNowRequested;

    /// <summary>User chose to continue later; App may close or hide the window.</summary>
    public event EventHandler? RestartLaterRequested;

    /// <summary>The setup run finished successfully; App may return to the everyday view.</summary>
    public event EventHandler? SetupFinished;

    /// <summary>The dashboard should switch to the setup screens.</summary>
    public event EventHandler? SetupActivated;

    /// <summary>The dashboard should return from the setup screens to everyday view.</summary>
    public event EventHandler? SetupDeactivated;

    #endregion

    #region Screen state

    [ObservableProperty]
    private SetupScreen _currentScreen = SetupScreen.Welcome;

    public SetupScreen CurrentSetupScreen => CurrentScreen;

    public bool ShowWelcome => CurrentScreen == SetupScreen.Welcome;

    public bool ShowChecking => CurrentScreen == SetupScreen.Checking;

    public bool ShowCannotRun => CurrentScreen == SetupScreen.CannotRun;

    public bool ShowRestartNeeded => CurrentScreen == SetupScreen.RestartNeeded;

    public bool ShowPasswordNeeded => CurrentScreen == SetupScreen.PasswordNeeded;

    public bool ShowDownloads => CurrentScreen == SetupScreen.Downloads;

    public bool ShowInstalling => CurrentScreen == SetupScreen.Installing;

    public bool ShowReady => CurrentScreen == SetupScreen.Ready;

    public bool ShowProblem => CurrentScreen == SetupScreen.Problem;

    public bool ShowStepper => CurrentScreen is SetupScreen.Checking or SetupScreen.Downloads or SetupScreen.Installing;

    public bool ShowQuietBanner => CurrentScreen == SetupScreen.Installing && Stuck;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQuietBanner))]
    private bool _stuck;
    [ObservableProperty]
    private int _overallPercent;

    [ObservableProperty]
    private string _activityText = string.Empty;

    [ObservableProperty]
    private string _stepElapsedText = string.Empty;

    [ObservableProperty]
    private string _failureHeadline = string.Empty;

    [ObservableProperty]
    private string _failureDetail = string.Empty;

    [ObservableProperty]
    private string _failureWhatToDo = string.Empty;

    /// <summary>Cached-parts summary shown on the Downloads screen (U15 extends this).</summary>
    [ObservableProperty]
    private string _cachedPartsLine = string.Empty;

    public string ReadyUsername => UiText.DefaultUsername;

    public string ReadyPassword => UiText.DefaultPassword;

    #endregion

    #region Welcome screen

    [ObservableProperty]
    private bool _startAtSignIn = true;

    public ObservableCollection<string> WelcomeFacts { get; } =
    [
        UiText.WelcomeTime,
        UiText.WelcomeSpace(PcThresholds.Default),
        UiText.WelcomeInternet,
        UiText.WelcomeKeepUsing,
    ];

    #endregion

    #region Progress strip

    public ObservableCollection<SetupStripItem> StripItems { get; }

    #endregion

    #region Details panel

    [ObservableProperty]
    private bool _detailsVisible;

    [ObservableProperty]
    private string _detailLogText = string.Empty;

    /// <summary>Header of the details expander (Show details / Hide details).</summary>
    public string DetailsHeaderText => DetailsVisible ? UiText.HideDetails : UiText.ShowDetails;

    public bool DetailLogPlaceholderVisible => DetailLogText.Length == 0;

    public bool DetailLogFilled => DetailLogText.Length > 0;

    public string DetailLogPlaceholder => DetailsPlaceholder;

    partial void OnDetailsVisibleChanged(bool value) => OnPropertyChanged(nameof(DetailsHeaderText));

    partial void OnDetailLogTextChanged(string value)
    {
        OnPropertyChanged(nameof(DetailLogPlaceholderVisible));
        OnPropertyChanged(nameof(DetailLogFilled));
    }

    #endregion

    #region Password screen

    [ObservableProperty]
    private string _passwordInput = string.Empty;

    [ObservableProperty]
    private bool _passwordWrongVisible;

    [ObservableProperty]
    private bool _isSubmittingPassword;

    #endregion

    partial void OnCurrentScreenChanged(SetupScreen value)
    {
        OnPropertyChanged(nameof(CurrentSetupScreen));
        OnPropertyChanged(nameof(ShowWelcome));
        OnPropertyChanged(nameof(ShowChecking));
        OnPropertyChanged(nameof(ShowCannotRun));
        OnPropertyChanged(nameof(ShowRestartNeeded));
        OnPropertyChanged(nameof(ShowPasswordNeeded));
        OnPropertyChanged(nameof(ShowDownloads));
        OnPropertyChanged(nameof(ShowInstalling));
        OnPropertyChanged(nameof(ShowReady));
        OnPropertyChanged(nameof(ShowProblem));
        OnPropertyChanged(nameof(ShowStepper));
        OnPropertyChanged(nameof(ShowQuietBanner));
    }


    #region Status/update mapping

    /// <summary>Applies a live setup progress report (presentation mapping only).</summary>
    public void ApplySetupUpdate(SetupUpdate update)
    {
        OverallPercent = update.OverallPercent;
        Stuck = update.Stuck;

        if (!string.IsNullOrEmpty(update.Activity))
        {
            ActivityText = UiText.NowLine(update.Activity);
        }

        StepElapsedText = UiText.TimeOnStep(update.StepElapsed);

        ApplySteps(update.Steps, update.CurrentStep);
        ApplyRunState(update);
        StartDetailsTail(update.RunId);
    }

    /// <summary>Applies an appliance status poll (presentation mapping only).</summary>
    public void ApplyStatus(ApplianceStatus status)
    {
        if (!string.IsNullOrEmpty(status.LoopbackUrl))
        {
            LoopbackUrl = status.LoopbackUrl;
        }
        if (status.PasswordRejected)
        {
            SetScreen(SetupScreen.PasswordNeeded);
            return;
        }

        if (status.Setup is { } setup)
        {
            if (setup.Finished)
            {
                SetScreen(SetupScreen.Ready);
                return;
            }

            if (setup.Working)
            {
                SetScreen(SetupScreen.Installing);
            }
        }
    }

    private void ApplySteps(IReadOnlyList<SetupStepStatus> steps, SetupStepId? currentStep)
    {
        if (steps.Count == 0)
        {
            return;
        }

        int? reached = null;
        foreach (var step in steps)
        {
            var position = UiText.StripPosition(step.Step);
            if (step.State is SetupStepState.Working or SetupStepState.Done or SetupStepState.Skipped)
            {
                reached = Math.Max(reached ?? 0, position);
            }
        }

        for (var i = 0; i < StripItems.Count; i++)
        {
            var item = StripItems[i];
            var position = i + 1;
            if (reached is null)
            {
                item.State = SetupStripState.Pending;
            }
            else if (position < reached.Value)
            {
                item.State = SetupStripState.Done;
            }
            else if (position == reached.Value)
            {
                item.State = currentStep is null ? SetupStripState.Done : SetupStripState.Working;
            }
            else
            {
                item.State = SetupStripState.Pending;
            }
        }
    }

    private void ApplyRunState(SetupUpdate update)
    {
        switch (update.Run)
        {
            case SetupRunState.Finished:
                SetScreen(SetupScreen.Ready);
                break;

            case SetupRunState.Failed:
            case SetupRunState.Cancelled:
                SetScreen(MapFailure(update.Failure));
                ApplyFailureText(update.Failure, update.FailureMessage);
                break;

            case SetupRunState.Working:
            default:
                SetScreen(MapWorking(update.CurrentStep));
                break;
        }
    }

    private SetupScreen MapFailure(FailureCode failure)
    {
        if (failure == FailureCode.RestartRequired)
        {
            return SetupScreen.RestartNeeded;
        }

        if (failure is FailureCode.PcCheckFinding or FailureCode.DiskFull)
        {
            return SetupScreen.CannotRun;
        }

        return SetupScreen.Problem;
    }

    private SetupScreen MapWorking(SetupStepId? currentStep) => currentStep switch
    {
        SetupStepId.PcCheck => SetupScreen.Checking,
        SetupStepId.Download => SetupScreen.Downloads,
        _ => SetupScreen.Installing,
    };

    #endregion

    #region Commands

    [RelayCommand]
    private Task StartSetupAsync() => RunSetupCoreAsync();

    [RelayCommand]
    private Task CheckAgainAsync() => RunSetupCoreAsync();

    [RelayCommand]
    private Task TryAgainAsync() => RunSetupCoreAsync();

    [RelayCommand]
    private void KeepWaiting()
    {
        KeepWaitingRequested?.Invoke(this, EventArgs.Empty);
        _service?.KeepWaiting();
    }

    [RelayCommand]
    private void TryAgainStep()
    {
        TryAgainStepRequested?.Invoke(this, EventArgs.Empty);
        _service?.TryAgainStep();
    }

    [RelayCommand]
    private void CancelSetup()
    {
        CancelRequested?.Invoke(this, EventArgs.Empty);
        _runCts?.Cancel();
    }

    [RelayCommand]
    private void RestartNow() => RestartNowRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RestartLater() => RestartLaterRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenErpNext()
    {
        var url = LoopbackUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            OpenUrlRequested?.Invoke(this, url);
        }
    }

    [RelayCommand]
    private void SaveDetails() => SaveDetailsRequested?.Invoke(this, DetailLogText);

    [RelayCommand]
    private void ToggleDetails() => DetailsVisible = !DetailsVisible;

    [RelayCommand]
    private async Task SubmitPasswordAsync(CancellationToken token)
    {
        if (_passwordSubmitInFlight || _runInFlight != 0 || _service is null)
        {
            // A password check must never hold the lifecycle lease while a run is active.
            return;
        }

        _passwordSubmitInFlight = true;
        IsSubmittingPassword = true;
        try
        {
            var password = PasswordInput ?? string.Empty;
            var result = await _service.VerifyPasswordAsync(password, token).ConfigureAwait(true);
            if (result.Outcome == OperationOutcome.Success)
            {
                PasswordWrongVisible = false;
                PasswordInput = string.Empty;
                await RunSetupCoreAsync().ConfigureAwait(true);
            }
            else
            {
                PasswordWrongVisible = true;
            }
        }
        finally
        {
            _passwordSubmitInFlight = false;
            IsSubmittingPassword = false;
        }
    }

    [RelayCommand]
    private void CancelPassword()
    {
        PasswordInput = string.Empty;
        PasswordWrongVisible = false;
        SetScreen(_previousScreenBeforePassword == SetupScreen.PasswordNeeded ? SetupScreen.Welcome : _previousScreenBeforePassword);
    }


    #endregion
    /// <summary>Shows the setup screens (activation without starting a run).</summary>
        var previous = CurrentScreen;
        if (screen == SetupScreen.PasswordNeeded)
        {
            _previousScreenBeforePassword = previous;
        }

        CurrentScreen = screen;
        if (screen == SetupScreen.Ready && previous != SetupScreen.Ready)
        {
            _passwordSubmitInFlight = false;
            SetupFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The loopback URL used by the Ready screen's open command.</summary>
    public string? LoopbackUrl { get; private set; }

    /// <summary>True while a RunSetupAsync call is in flight (or its run is still active).</summary>
    public bool SetupActive => _setupActive || Volatile.Read(ref _runInFlight) == 1;

    /// <summary>Late service attach (App composition root wires Core after construction).</summary>
    public void AttachService(IApplianceService service)
    {
        _service = service;
    }

    /// <summary>Reads new lines from the current setup log into the details text; called on a UI timer.</summary>
    public void RefreshDetails()
    {
        if (_tailReader is null)
        {
            return;
        }

        foreach (var line in _tailReader.ReadNew())
        {
            if (_detailsBuffer.Length > 0)
            {
                _detailsBuffer.AppendLine();
            }

            _detailsBuffer.Append(line);
        }

        DetailLogText = _detailsBuffer.ToString();
    }


    /// <summary>
    /// Starts (or resumes) one setup run. Exactly one <see cref="IApplianceService.RunSetupAsync"/>
    /// call per start; further clicks while a run is in flight do nothing.
    /// </summary>
    public Task RunSetupAsync() => RunSetupCoreAsync();

    private async Task RunSetupCoreAsync()
    {
        if (_service is null || Interlocked.CompareExchange(ref _runInFlight, 1, 0) != 0)
        {
            return;
        }

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        SetupStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            await _runGate.WaitAsync(token).ConfigureAwait(true);
            try
            {
                SetScreen(SetupScreen.Installing);
                var progress = new Progress<SetupUpdate>(u => Dispatcher.UIThread.Post(() => ApplySetupUpdate(u)));
                var result = await _service.RunSetupAsync(new SetupRequest(StartAtSignIn), progress, token).ConfigureAwait(true);
                ApplySetupResult(result);
            }
            finally
            {
                _runGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            SetScreen(SetupScreen.Problem);
            ApplyFailureText(FailureCode.Cancelled, null);
        }
        finally
        {
            Interlocked.Exchange(ref _runInFlight, 0);
            OnPropertyChanged(nameof(SetupActive));
        }
    }

    private void ApplySetupResult(SetupResult result)
    {
        switch (result.Outcome)
        {
            case OperationOutcome.Success:
                SetScreen(SetupScreen.Ready);
                break;

            default:
                SetScreen(MapFailure(result.Code));
                ApplyFailureText(result.Code, null);
                break;
        }
    }

    private void ApplyFailureText(FailureCode failure, string? failureMessage)
    {
        var f = UiText.Failure(failure == FailureCode.None ? FailureCode.Unknown : failure);
        FailureHeadline = f.Headline;
        FailureDetail = failureMessage ?? f.Detail;
        FailureWhatToDo = f.WhatToDo;
    }

    private void StartDetailsTail(Guid runId)
    {
        if (runId == _currentRunId && _tailReader is not null)
        {
            return;
        }

        _currentRunId = runId;
        var path = Path.Combine(KnownPaths.LogsDir, $"setup-{runId:N}.log");
        _tailReader = new LogTailReader(path);
        _detailsBuffer.Clear();
        DetailLogText = string.Empty;
    }

    /// <summary>Points the details tail at an explicit log path (e.g. from status).</summary>
    public void UseLogPath(string path)
    {
        _tailReader = new LogTailReader(path);
        _detailsBuffer.Clear();
        DetailLogText = string.Empty;
    }
}
