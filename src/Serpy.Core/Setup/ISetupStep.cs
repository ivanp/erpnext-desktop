using Serpy.Core.Contracts;

namespace Serpy.Core.Setup;

/// <summary>
/// One step of setup (KTD7). A step has three duties: say whether its result already exists, run,
/// and delete its own leftovers. It never talks to the screen; it reports through its context.
/// </summary>
public interface ISetupStep
{
    SetupStepId Id { get; }

    /// <summary>The step's share of the overall progress bar.</summary>
    int Weight { get; }

    /// <summary>No activity for this long raises the stuck flag (KTD8).</summary>
    TimeSpan QuietTime { get; }

    /// <summary>
    /// True when the step's result exists under its final name and passes its validation, so the
    /// step is skipped. A file under a pending or staging name never counts. A step may record an
    /// already valid result in appliance state here, in place of running.
    /// </summary>
    bool IsDone(SetupContext context);

    Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct);

    /// <summary>Deletes what an interrupted run of this step left behind (KTD10).</summary>
    void DeleteLeftovers(SetupContext context);
}

public sealed record SetupStepResult(
    OperationOutcome Outcome,
    FailureCode Code,
    string Message,
    string? LogPath = null)
{
    public static SetupStepResult From(OperationResult result) =>
        new(result.Outcome, result.Code, result.Message, result.LogPath);

    public static SetupStepResult Success(string message = "", string? logPath = null) =>
        new(OperationOutcome.Success, FailureCode.None, message, logPath);

    public static SetupStepResult Failed(FailureCode code, string message, string? logPath = null) =>
        new(OperationOutcome.Failure, code, message, logPath);
}

/// <summary>What a running step can see and say.</summary>
public sealed class SetupContext
{
    public required Guid RunId { get; init; }

    /// <summary>
    /// True while an adoption or recovery journal is open after reconciliation. Its pending and
    /// backup files belong to the journal, so no step deletes any leftover then (KTD10).
    /// </summary>
    public required bool JournalOpen { get; init; }

    /// <summary>One line of what the step is doing. Counts as activity for the stuck check.</summary>
    public required Action<string> Activity { get; init; }

    /// <summary>The step's own progress from 0 to 100. Counts as activity.</summary>
    public required Action<int> StepPercent { get; init; }

    /// <summary>One line for the setup log. Not shown on the screen.</summary>
    public required Action<string> Log { get; init; }

    /// <summary>The PC check report, for the details file. Set by the PC check step.</summary>
    public string? PcCheckReport { get; set; }

    /// <summary>True when the PC check found that Prepare must turn the Windows feature on. U7 reads it.</summary>
    public bool NeedsFeature { get; set; }

    /// <summary>Adapts an operation's progress stream to this context, with no thread hop.</summary>
    public IProgress<OperationUpdate> AsProgress() => new InlineProgress(this);

    private sealed class InlineProgress(SetupContext context) : IProgress<OperationUpdate>
    {
        public void Report(OperationUpdate update)
        {
            if (!string.IsNullOrWhiteSpace(update.Message)) context.Activity(update.Message.TrimEnd());
            if (update.ProgressPercent is { } percent) context.StepPercent(percent);
        }
    }
}
