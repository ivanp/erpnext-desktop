namespace Serpy.Core.Contracts;

/// <summary>
/// Readiness dimension: reflects what has been durably committed to disk.
/// Transitions are monotonic: NotBuilt → Built → Initialized.
/// Recovery is a self-transition on Initialized (system.qcow2 replaced, data.img retained).
/// </summary>
public enum ReadinessState
{
    NotBuilt = 0,
    Built = 1,
    Initialized = 2,
}

/// <summary>
/// Runtime health dimension: only meaningful when QEMU may be running.
/// </summary>
public enum HealthState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    RunningUnhealthy = 3,
    Crashed = 4,
}

/// <summary>
/// Recommended first-run / launch routing signal for the VM.
/// </summary>
public enum LaunchRoute
{
    Start = 0,
    Setup = 1,
}

/// <summary>
/// Combined, observable surface status the UI binds to.
/// </summary>
public sealed record ApplianceStatus(
    ReadinessState Readiness,
    HealthState Health,
    string? LoopbackUrl,
    string? CurrentStage,
    OperationKind? ActiveOperation,
    int? ProgressPercent,
    bool HasCommittedDataOnDisk = false,
    bool ArchiveHasProvenanceRecord = false,
    LaunchRoute RecommendedRoute = LaunchRoute.Start);

public enum OperationKind
{
    Build,
    Initialize,
    Start,
    Stop,
    Restart,
    Recover,
    BuildPreservingData,
    InspectArchive,
    Adopt,
}
