namespace Serpy.Core.Contracts;

/// <summary>
/// Single operation surface for the appliance lifecycle.
/// ViewModels call this; no other code owns lifecycle logic.
/// All operations accept cancellation and stream OperationUpdate events.
/// </summary>
public interface IApplianceService
{
    /// <summary>
    /// Download, verify, and provision system.qcow2. Runs R9/R10 version and build gates.
    /// Valid from: ReadinessState.NotBuilt
    /// </summary>
    Task<OperationResult> BuildAsync(
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// Download, verify, and provision system.qcow2 while preserving an existing data.img if present.
    /// Routes around CanBuildFrom; does not migrate or adopt data (that is AdoptAsync).
    /// </summary>
    Task<OperationResult> BuildPreservingDataAsync(
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// Create data.img, initialize MariaDB datadir, create first Frappe site.
    /// Runs R11 functional health check before committing.
    /// Valid from: ReadinessState.Built
    /// </summary>
    Task<OperationResult> InitializeAsync(
        InitializationParameters parameters,
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// Start QEMU/WHPX, wait for healthy appliance, expose loopback URL.
    /// Valid from: ReadinessState.Initialized, HealthState.Stopped | Crashed
    /// </summary>
    Task<OperationResult> StartAsync(
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// Graceful QMP/ACPI powerdown; confirms SHUTDOWN event or process exit.
    /// Valid from: HealthState.Running | RunningUnhealthy
    /// </summary>
    Task<OperationResult> StopAsync(
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// Confirmed stop then start; uses the shared service, not a tray shortcut.
    /// </summary>
    Task<OperationResult> RestartAsync(
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// Replace system.qcow2 with replacementImagePath while retaining data.img.
    /// Rejects downgrades. Runs mariadb-upgrade if required, then bench migrate.
    /// Records and completes each phase in the recovery journal.
    /// Valid from: ReadinessState.Initialized, HealthState.Stopped
    /// </summary>
    Task<OperationResult> RecoverAsync(
        string replacementImagePath,
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// One-time non-mutating inspection of an archive for provenance and version compatibility.
    /// Decrypts DPAPI metadata only; never reads the archive file body.
    /// </summary>
    Task<ArchiveInspection> InspectArchiveAsync(
        string archivePath,
        CancellationToken ct = default);

    /// <summary>
    /// Migrate and activate an archived dataset via a disposable working copy.
    /// On health pass + clean halt, atomically promotes to data.img (journaled).
    /// On failure, discards copy, persists nothing, archive is retained untouched.
    /// </summary>
    Task<OperationResult> AdoptAsync(
        AdoptParameters parameters,
        ArchiveInspection inspection,
        bool legacyConsentApproved,
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// Non-mutating status read. Never acquires the lifecycle lease.
    /// </summary>
    Task<ApplianceStatus> GetStatusAsync(CancellationToken ct = default);
}
