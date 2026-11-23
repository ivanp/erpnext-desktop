using Serpy.Core.Downloads;

using Serpy.Core.Setup;

namespace Serpy.Core.Contracts;

/// <summary>The steps of one setup run, in the order they run (KTD7).</summary>
public enum SetupStepId
{
    PcCheck,
    Prepare,
    Download,
    Build,
    CompanyData,
    Start,
}

/// <summary>What one step is doing. Skipped means its result already existed and was valid.</summary>
public enum SetupStepState
{
    Pending,
    Working,
    Done,
    Skipped,
    Failed,
}

public enum SetupRunState
{
    Working,
    Finished,
    Failed,
    Cancelled,
}

/// <summary>How setup gets company data (KTD11). The route is decided once, before Build.</summary>
public enum CompanyDataRoute
{
    Undecided,
    CreateNew,
    AdoptExisting,
}

public sealed record SetupStepStatus(SetupStepId Step, SetupStepState State);

/// <summary>The choices the user makes before setup starts.</summary>
public sealed record SetupRequest(bool StartAtSignIn = true);

/// <summary>
/// One snapshot of a running setup, which is everything the setup screens draw (KTD8). Updates
/// arrive at every state change and at least once per tick while a step works, so the screen
/// keeps moving even when a step prints nothing.
/// </summary>
public sealed record SetupUpdate(
    Guid RunId,
    SetupRunState Run,
    IReadOnlyList<SetupStepStatus> Steps,
    SetupStepId? CurrentStep,
    int OverallPercent,
    string? Activity,
    TimeSpan StepElapsed,
    bool Stuck,
    FailureCode Failure = FailureCode.None,
    string? FailureMessage = null,
    IReadOnlyList<PartDownloadSnapshot>? DownloadParts = null,
    IReadOnlyList<PcCheckLine>? CheckFindings = null);

/// <summary>Terminal result of one setup run.</summary>
public sealed record SetupResult(
    Guid RunId,
    OperationOutcome Outcome,
    FailureCode Code,
    SetupStepId? FailedStep,
    string Message,
    IReadOnlyList<string> LogPaths);

/// <summary>What status shows about setup: finished or not, working or not, and why it last failed.</summary>
public sealed record SetupSummary(bool Finished, bool Working, FailureCode LastFailure);
