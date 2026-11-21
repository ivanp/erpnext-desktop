using Serpy.Core.Downloads;

using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Setup;
using Serpy.Core.Versions;

namespace Serpy.Core.Operations;

/// <summary>
/// Runs the whole setup from the first step to Ready (KTD6). The caller holds the lifecycle lease
/// for the whole run, and this class calls the step operations directly, so a service entry guard
/// cannot refuse a route that the company data table chose.
///
/// Files decide what runs (KTD7): a step whose result exists under its final name and validates is
/// skipped. The setup state file is only for what files cannot show, and its step status is for the
/// screen. A run that dies at any point therefore resumes at the interrupted step.
/// </summary>
public sealed class SetupOperation
{
    private readonly IReadOnlyList<ISetupStep> _steps;
    private readonly SetupStateStore _setupStore;
    private readonly StateStore _stateStore;
    private readonly IPowerRequest _power;
    private readonly Action _reconcileJournals;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _tick;
    private readonly TimeSpan _minEmitGap;
    private readonly VersionManifest? _manifest;
    private readonly Func<Guid, string> _logPathFor;

    private readonly object _gate = new();
    private int _working;

    // Everything below is guarded by _gate.
    private Dictionary<SetupStepId, SetupStepState> _status = [];
    private IProgress<SetupUpdate>? _progress;
    private Guid _runId;
    private SetupStepId? _current;
    private DateTimeOffset _stepStarted;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _lastEmit;
    private string? _activity;
    private int _stepPercent;
    private bool _stuck;
    private CancellationTokenSource? _stepCts;
    private bool _restartRequested;
    private List<string> _lastLogPaths = [];
    private string? _lastPcCheckReport;
    private IReadOnlyList<PartDownloadSnapshot>? _downloadParts;

    public SetupOperation(
        IReadOnlyList<ISetupStep> steps,
        SetupStateStore setupStore,
        StateStore stateStore,
        IPowerRequest power,
        Action reconcileJournals,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? tick = null,
        TimeSpan? minEmitGap = null,
        VersionManifest? manifest = null,
        Func<Guid, string>? logPathFor = null)
    {
        _steps = steps;
        _setupStore = setupStore;
        _stateStore = stateStore;
        _power = power;
        _reconcileJournals = reconcileJournals;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tick = tick ?? TimeSpan.FromSeconds(1);
        _minEmitGap = minEmitGap ?? TimeSpan.FromMilliseconds(100);
        _manifest = manifest;
        _logPathFor = logPathFor ?? (id => Path.Combine(KnownPaths.LogsDir, $"setup-{id:N}.log"));
    }

    /// <summary>True from the start of a run to its end, which Status shows as "working".</summary>
    public bool IsWorking => Volatile.Read(ref _working) != 0;

    /// <summary>Logs of the latest run, for Save details.</summary>
    public IReadOnlyList<string> LastLogPaths
    {
        get { lock (_gate) return [.. _lastLogPaths]; }
    }

    /// <summary>The report of the latest PC check, for Save details.</summary>
    public string? LastPcCheckReport
    {
        get { lock (_gate) return _lastPcCheckReport; }
    }

    public async Task<SetupResult> RunAsync(
        SetupRequest request, IProgress<SetupUpdate> progress, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        var logPath = _logPathFor(runId);
        using var log = new OperationLog(logPath);
        Action<string> reportProblem = message => log.Info(message);
        _setupStore.Problem += reportProblem;
        Volatile.Write(ref _working, 1);

        lock (_gate)
        {
            _runId = runId;
            _progress = progress;
            _status = _steps.ToDictionary(s => s.Id, _ => SetupStepState.Pending);
            _current = null;
            _lastLogPaths = [logPath];
            _lastPcCheckReport = null;
        }

        IDisposable? powerHold = null;
        try
        {
            // Journals first: nothing is skipped or deleted before an interrupted adoption or recovery
            // has been rolled back or finished (KTD7).
            try { _reconcileJournals(); }
            catch (Exception ex) { log.Error("Journal reconciliation failed", ex); }

            var applianceState = _stateStore.Read();
            var journalOpen = applianceState.AdoptionJournal is not null ||
                              applianceState.RecoveryJournal is { HealthPassed: false };
            if (journalOpen) log.Info("A recovery or adoption journal is open; no leftover will be deleted.");

            _setupStore.Mutate(s =>
            {
                s.StartAtSignIn = request.StartAtSignIn;
                s.Finished = false;
                s.LastFailure = FailureCode.None;
                s.LastFailedStep = null;
            });

            var context = new SetupContext
            {
                RunId = runId,
                JournalOpen = journalOpen,
                Activity = NoteActivity,
                StepPercent = percent => NoteStepPercent(percent),
                Log = line => log.Info(line),
                DownloadPartsUpdated = parts =>
                {
                    lock (_gate)
                    {
                        _downloadParts = parts;
                        EmitLocked(SetupRunState.Working, force: false);
                    }
                },
            };

            Emit(SetupRunState.Working, force: true);
            foreach (var step in _steps)
            {
                var preflight = step.Preflight(context);
                if (preflight is { Outcome: OperationOutcome.Failure })
                {
                    log.Stage(step.Id.ToString(), $"Preflight failed: {preflight.Message}");
                    SetStatus(step.Id, SetupStepState.Failed);
                    _setupStore.Mutate(s =>
                    {
                        s.LastFailure = preflight.Code;
                        s.LastFailedStep = step.Id;
                    });
                    Emit(SetupRunState.Failed, preflight.Code, preflight.Message, force: true);
                    return Result(runId, OperationOutcome.Failure, preflight.Code, step.Id, preflight.Message);
                }
            }


            foreach (var step in _steps)
            {
                ct.ThrowIfCancellationRequested();

                if (step.IsDone(context))
                {
                    log.Stage(step.Id.ToString(), "Already done; skipped.");
                    SetStatus(step.Id, SetupStepState.Skipped);
                    continue;
                }

                // The first step that works takes the power request. A run that only skips steps
                // never keeps the PC awake.
                powerHold ??= _power.Hold();

                var outcome = await RunStepAsync(step, context, log, ct);
                lock (_gate) _lastPcCheckReport = context.PcCheckReport ?? _lastPcCheckReport;
                if (outcome.Outcome == OperationOutcome.Success)
                {
                    if (!string.IsNullOrEmpty(outcome.LogPath)) AddLog(outcome.LogPath);
                    SetStatus(step.Id, SetupStepState.Done);
                    continue;
                }

                if (!string.IsNullOrEmpty(outcome.LogPath)) AddLog(outcome.LogPath);

                if (outcome.Outcome == OperationOutcome.Cancelled || ct.IsCancellationRequested)
                {
                    log.Stage(step.Id.ToString(), "Cancelled.");
                    SetStatus(step.Id, SetupStepState.Pending);
                    Emit(SetupRunState.Cancelled, force: true);
                    return Result(runId, OperationOutcome.Cancelled, FailureCode.Cancelled, step.Id,
                        "Setup was cancelled.");
                }

                log.Stage(step.Id.ToString(), $"Failed: {outcome.Message}");
                SetStatus(step.Id, SetupStepState.Failed);
                _setupStore.Mutate(s =>
                {
                    s.LastFailure = outcome.Code;
                    s.LastFailedStep = step.Id;
                });
                Emit(SetupRunState.Failed, outcome.Code, outcome.Message, force: true);
                return Result(runId, OperationOutcome.Failure, outcome.Code, step.Id, outcome.Message);
            }

            _setupStore.Mutate(s => s.Finished = true);
            lock (_gate) _current = null;
            Emit(SetupRunState.Finished, force: true);
            return Result(runId, OperationOutcome.Success, FailureCode.None, null, "Setup is finished.");
        }
        catch (OperationCanceledException)
        {
            log.Info("Setup was cancelled.");
            Emit(SetupRunState.Cancelled, force: true);
            return Result(runId, OperationOutcome.Cancelled, FailureCode.Cancelled, null, "Setup was cancelled.");
        }
        catch (Exception ex)
        {
            log.Error("Setup failed", ex);
            _setupStore.Mutate(s => s.LastFailure = FailureCode.Unknown);
            Emit(SetupRunState.Failed, FailureCode.Unknown, ex.Message, force: true);
            return Result(runId, OperationOutcome.Failure, FailureCode.Unknown, null, ex.Message);
        }
        finally
        {
            powerHold?.Dispose();
            _setupStore.Problem -= reportProblem;
            lock (_gate)
            {
                _current = null;
            }

            Volatile.Write(ref _working, 0);
        }
    }

    /// <summary>
    /// The user chose Keep waiting on a quiet step. The timer starts again and the flag clears;
    /// the step goes on working (KTD8).
    /// </summary>
    public void KeepWaiting()
    {
        lock (_gate)
        {
            if (_current is null) return;
            _lastActivity = _clock();
            _stuck = false;
            EmitLocked(SetupRunState.Working, force: true);
        }
    }

    /// <summary>The user chose Try again on a quiet step: it is cancelled and run again from its start.</summary>
    public void TryAgainStep()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_current is null) return;
            _restartRequested = true;
            cts = _stepCts;
        }

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the step just ended */ }
    }

    /// <summary>The Save details text for the latest run.</summary>
    public string ComposeDetails()
    {
        string? pcReport;
        IReadOnlyList<string> logs;
        lock (_gate)
        {
            pcReport = _lastPcCheckReport;
            logs = [.. _lastLogPaths];
        }

        return DetailsReport.Compose(pcReport, _setupStore.Read(), _stateStore.Read(), _manifest, logs, _clock());
    }

    // ── one step ─────────────────────────────────────────────────────────────

    private async Task<SetupStepResult> RunStepAsync(
        ISetupStep step, SetupContext context, OperationLog log, CancellationToken ct)
    {
        while (true)
        {
            // A step that was cut short left files behind. The step that owns them deletes them first,
            // and never while a journal owns them.
            if (!context.JournalOpen) step.DeleteLeftovers(context);

            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate)
            {
                _current = step.Id;
                _stepCts = stepCts;
                _restartRequested = false;
                _stepStarted = _clock();
                _lastActivity = _stepStarted;
                _activity = null;
                _stepPercent = 0;
                _stuck = false;
                _status[step.Id] = SetupStepState.Working;
            }

            SaveStatus();
            log.Stage(step.Id.ToString(), "Started.");
            Emit(SetupRunState.Working, force: true);

            var work = RunGuarded(step, context, log, stepCts.Token);
            using var ticking = new CancellationTokenSource();
            while (!work.IsCompleted)
            {
                var tickDelay = Task.Delay(_tick, ticking.Token);
                await Task.WhenAny(work, tickDelay).ConfigureAwait(false);
                if (work.IsCompleted) break;

                lock (_gate)
                {
                    if (!_stuck && _clock() - _lastActivity >= step.QuietTime)
                    {
                        _stuck = true;
                        log.Info($"No activity for {step.QuietTime.TotalMinutes:0} minutes; the {step.Id} step may be stuck.");
                    }

                    EmitLocked(SetupRunState.Working, force: true);
                }
            }

            await ticking.CancelAsync().ConfigureAwait(false);
            var result = await work.ConfigureAwait(false);

            bool restart;
            lock (_gate)
            {
                restart = _restartRequested && !ct.IsCancellationRequested;
                _stepCts = null;
            }

            if (!restart) return result;

            log.Stage(step.Id.ToString(), "Try again was chosen; starting the step over.");
        }
    }

    private static async Task<SetupStepResult> RunGuarded(
        ISetupStep step, SetupContext context, OperationLog log, CancellationToken ct)
    {
        try
        {
            return await step.RunAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SetupStepResult(OperationOutcome.Cancelled, FailureCode.Cancelled, "Cancelled.");
        }
        catch (Exception ex)
        {
            log.Error($"The {step.Id} step failed", ex);
            return SetupStepResult.Failed(FailureCode.Unknown, ex.Message);
        }
    }

    // ── updates ──────────────────────────────────────────────────────────────

    private void NoteActivity(string text)
    {
        lock (_gate)
        {
            _activity = text;
            _lastActivity = _clock();
            _stuck = false;
            EmitLocked(SetupRunState.Working, force: false);
        }
    }

    private void NoteStepPercent(int percent)
    {
        lock (_gate)
        {
            _stepPercent = Math.Clamp(percent, 0, 100);
            _lastActivity = _clock();
            _stuck = false;
            EmitLocked(SetupRunState.Working, force: false);
        }
    }

    private void SetStatus(SetupStepId id, SetupStepState state)
    {
        lock (_gate) _status[id] = state;
        SaveStatus();
        Emit(SetupRunState.Working, force: true);
    }

    private void SaveStatus()
    {
        Dictionary<SetupStepId, SetupStepState> copy;
        lock (_gate) copy = new(_status);
        _setupStore.Mutate(s => s.Steps = copy);
    }

    private void AddLog(string path)
    {
        lock (_gate)
        {
            if (!_lastLogPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) _lastLogPaths.Add(path);
        }
    }

    private void Emit(SetupRunState run, FailureCode failure = FailureCode.None, string? message = null, bool force = false)
    {
        lock (_gate) EmitLocked(run, force, failure, message);
    }

    private void EmitLocked(
        SetupRunState run, bool force, FailureCode failure = FailureCode.None, string? message = null)
    {
        var now = _clock();
        if (!force && now - _lastEmit < _minEmitGap) return;
        _lastEmit = now;

        var steps = _steps.Select(s => new SetupStepStatus(s.Id, _status.GetValueOrDefault(s.Id))).ToList();
        var total = _steps.Sum(s => s.Weight);
        var finished = _steps.Where(s => _status.GetValueOrDefault(s.Id) is SetupStepState.Done or SetupStepState.Skipped)
            .Sum(s => s.Weight);
        var working = _current is { } id ? _steps.FirstOrDefault(s => s.Id == id) : null;
        var partial = working is null ? 0 : working.Weight * _stepPercent / 100;
        var percent = run == SetupRunState.Finished ? 100
            : total == 0 ? 0 : Math.Clamp((finished + partial) * 100 / total, 0, 99);

        _progress?.Report(new SetupUpdate(
            _runId, run, steps, _current, percent, _activity,
            _current is null ? TimeSpan.Zero : now - _stepStarted,
            _stuck, failure, message, _downloadParts));
    }

    private SetupResult Result(
        Guid runId, OperationOutcome outcome, FailureCode code, SetupStepId? failedStep, string message) =>
        new(runId, outcome, code, failedStep, message, LastLogPaths);
}
