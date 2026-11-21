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
    /// Stop running VM if any, wipe appliance disks, state, and credentials while preserving runtime binaries and base image.
    /// </summary>
    Task<OperationResult> ResetAsync(
        IProgress<OperationUpdate> progress,
        CancellationToken ct = default);

    /// <summary>
    /// Runs the whole setup from the first step to Ready (KTD6). Takes the lifecycle lease once for
    /// the whole run, so every other mutation returns the busy result meanwhile. Steps whose result
    /// already exists are skipped, so an interrupted run resumes where it stopped.
    /// </summary>
    Task<SetupResult> RunSetupAsync(
        SetupRequest request,
        IProgress<SetupUpdate> progress,
        CancellationToken ct = default);

    /// <summary>The user chose Keep waiting on a step that went quiet: the timer starts again.</summary>
    void KeepWaiting();

    /// <summary>The user chose Try again on a step that went quiet: the step starts over.</summary>
    void TryAgainStep();

    /// <summary>The Save details text for the latest setup run. It never holds a credential.</summary>
    string ComposeSetupDetails();

    /// <summary>
    /// Verifies a submitted Administrator password against the running appliance, saves it
    /// to encrypted storage on success, and re-runs the health check (KTD12).
    /// </summary>
    Task<OperationResult> VerifyPasswordAsync(string password, CancellationToken ct = default);

    /// <summary>
    /// Non-mutating status read. Never acquires the lifecycle lease.
    /// </summary>
    Task<ApplianceStatus> GetStatusAsync(CancellationToken ct = default);
}
