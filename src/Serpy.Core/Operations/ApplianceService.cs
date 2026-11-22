using Serpy.Core.Downloads;

using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Health;
using Serpy.Core.Images;
using Serpy.Core.Qemu;
using Serpy.Core.Setup;
using Serpy.Core.Versions;

namespace Serpy.Core.Operations;

/// <summary>
/// Single lifecycle service that the GUI calls (IApplianceService).
/// All mutating operations are serialised through LifecycleLock.
/// Status reads never acquire the lock.
/// Lifecycle rules live here; ViewModels must never duplicate them.
/// </summary>
public sealed class ApplianceService : IApplianceService, IDisposable
{
    private readonly LifecycleLock _lock;
    private readonly StateStore _stateStore;
    private readonly StatusOperation _statusOp;
    private readonly BuildOperation _buildOp;
    private readonly InitializeOperation _initOp;
    private readonly StartOperation _startOp;
    private readonly StopOperation _stopOp;
    private readonly RecoverOperation _recoverOp;
    private readonly InspectArchiveOperation _inspectArchiveOp;
    private readonly AdoptOperation _adoptOp;
    private readonly ResetOperation _resetOp;
    private readonly SetupOperation _setupOp;
    private readonly HealthCredentials _healthCredentials;
    private readonly TlsCertificateStore _certStore;
    private readonly ApplianceSettings _settings;
    private readonly Func<string, string, HealthCredentials, Health.HealthChecker>? _healthCheckerFactory;
    public ApplianceService(
        ManagedRuntimeResolver runtimeResolver,
        TlsCertificateStore certStore,
        QemuImageTool imageTool,
        BaseImageDownloader imageDownloader,
        NoCloudSeedWriter seedWriter,
        HealthCredentials healthCredentials,
        StateStore stateStore,
        VersionManifest manifest,
        ApplianceSettings settings,
        IPowerRequest? powerRequest = null,
        IPcProbe? pcProbe = null,
        Func<string, string, HealthCredentials, Health.HealthChecker>? healthCheckerFactory = null)
    {
        _stateStore = stateStore;
        _healthCredentials = healthCredentials;
        _certStore = certStore;
        _settings = settings ?? new ApplianceSettings();
        _healthCheckerFactory = healthCheckerFactory;
        _lock      = new LifecycleLock();
        var setupStore = new SetupStateStore();
        _statusOp  = new StatusOperation(stateStore, setupStore, () => _setupOp?.IsWorking ?? false);
        BuildInputs buildInputs;
        try { buildInputs = BuildInputsLoader.Load(); }
        catch { buildInputs = new BuildInputs(); }
        var downloadCache = new DownloadCache();
        var downloadEngine = new DownloadEngine(buildInputs, downloadCache);
        if (runtimeResolver is { } rr)
        {
            rr.DownloadEngine ??= downloadEngine;
        }
        _buildOp   = new BuildOperation(
            imageDownloader, seedWriter, imageTool,
            runtimeResolver, certStore, stateStore, manifest, _settings,
            buildInputs, downloadCache);
        _initOp    = new InitializeOperation(
            imageTool, runtimeResolver, certStore,
            healthCredentials, stateStore, _settings);
        _startOp   = new StartOperation(
            runtimeResolver, certStore, healthCredentials, stateStore, _settings);
        _stopOp    = new StopOperation(stateStore, certStore);
        _recoverOp = new RecoverOperation(
            imageTool, runtimeResolver, certStore,
            healthCredentials, stateStore, manifest, _settings);
        _inspectArchiveOp = new InspectArchiveOperation(manifest);
        _adoptOp   = new AdoptOperation(
            imageTool, runtimeResolver, certStore,
            healthCredentials, stateStore, manifest, _settings);
        _resetOp   = new ResetOperation(
            _stopOp, healthCredentials, stateStore, setupStore);
        AdoptOperation.ReconcileJournal(stateStore, healthCredentials);
        // The PC check comes first and runs again on every run. Only Windows has a probe today;
        // the other platforms are compile-only targets, so without a probe the run has no PC check
        // and no Prepare step (Prepare installs QEMU and turns on the Windows features, both Windows work).
        // Prepare follows the PC check because it acts on what the check found, and it is the only
        // step that asks for administrator permission (KTD14).
        List<ISetupStep> steps = [];
        if (pcProbe is not null)
        {
            steps.Add(new PcCheckStep(
                pcProbe,
                () => KnownPaths.ApplianceDir,
                () => DownloadHosts.StillNeeded(buildInputs, KnownPaths.ApplianceDir, downloadCache),
                extraDiskBytes: () => CompanyDataStep.GetExtraDiskBytes(
                    KnownPaths.ApplianceDir, setupStore, stateStore)));
            steps.Add(new PrepareStep(runtimeResolver, setupStore, engine: downloadEngine));
        }

        steps.Add(new DownloadStep(downloadEngine, downloadCache));

        steps.Add(new BuildStep(_buildOp, stateStore, setupStore, settings: _settings));
        steps.Add(new CompanyDataStep(
            _initOp, stateStore, _adoptOp, _inspectArchiveOp, setupStore, healthCredentials));
        steps.Add(new StartStep(_startOp, stateStore, settings: _settings));
        _setupOp   = new SetupOperation(
            steps,
            setupStore, stateStore,
            powerRequest ?? (OperatingSystem.IsWindows() ? new Platform.Windows.PowerRequest() : NoPowerRequest.Instance),
            () => AdoptOperation.ReconcileJournal(stateStore, healthCredentials),
            manifest: manifest);
    }

    // ── Status (non-mutating, no lock) ────────────────────────────────────────

    public Task<ApplianceStatus> GetStatusAsync(CancellationToken ct = default) =>
        _statusOp.GetStatusAsync(ct);

    public async Task<OperationResult> BuildAsync(
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.Build);
        using (lease)
        {
            if (!CanBuildFrom(_stateStore.Read(), HasCommittedDataOnDisk()))
                return InvalidReadiness(OperationKind.Build, "not built or site-less built without persistent data", "Use Recover to replace an initialized system image.");
            return await _buildOp.ExecuteAsync(Guid.NewGuid(), progress, ct);
        }
    }

    public async Task<OperationResult> BuildPreservingDataAsync(
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.BuildPreservingData);
        using (lease)
        {
            if (!CanBuildPreservingDataFrom(_stateStore.Read(), HasCommittedDataOnDisk()))
                return InvalidReadiness(OperationKind.BuildPreservingData, "data disk present", "Use Build to create a fresh system image when no data disk exists.");
            return await _buildOp.ExecuteAsync(Guid.NewGuid(), progress, preserveData: true, ct);
        }
    }

    public async Task<OperationResult> InitializeAsync(
        Contracts.InitializationParameters parameters,
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        if (!Contracts.InitializationParameters.IsValidSiteName(parameters.SiteName))
            return new OperationResult(Guid.NewGuid(), OperationKind.Initialize,
                OperationOutcome.Failure,
                "Site name must be a lowercase fully qualified domain name (for example, site1.local).");

        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.Initialize);
        using (lease)
        {
            if (_stateStore.Read().Readiness != ReadinessState.Built)
                return InvalidReadiness(OperationKind.Initialize, "built", "Build the system image before initialization.");
            return await _initOp.ExecuteAsync(
                Guid.NewGuid(), parameters.SiteName, parameters.AdminPassword, progress, ct);
        }
    }

    public async Task<OperationResult> StartAsync(
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.Start);
        using (lease)
            return await _startOp.ExecuteAsync(Guid.NewGuid(), progress, ct);
    }

    public async Task<OperationResult> StopAsync(
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.Stop);
        using (lease)
            return await _stopOp.ExecuteAsync(Guid.NewGuid(), progress, ct);
    }

    public async Task<OperationResult> RestartAsync(
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.Restart);
        using (lease)
        {
            var id = Guid.NewGuid();
            var stopResult = await _stopOp.ExecuteAsync(id, progress, ct);
            if (stopResult.Outcome != OperationOutcome.Success)
                return stopResult with { Kind = OperationKind.Restart };
            return (await _startOp.ExecuteAsync(id, progress, ct)) with
            {
                Kind = OperationKind.Restart,
            };
        }
    }

    public async Task<OperationResult> RecoverAsync(
        string replacementImagePath,
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.Recover);
        using (lease)
            return await _recoverOp.ExecuteAsync(Guid.NewGuid(), replacementImagePath, progress, ct);
    }

    public Task<ArchiveInspection> InspectArchiveAsync(
        string archivePath, CancellationToken ct = default)
    {
        return _inspectArchiveOp.ExecuteAsync(archivePath, ct);
    }

    public async Task<OperationResult> AdoptAsync(
        AdoptParameters parameters,
        ArchiveInspection inspection,
        bool legacyConsentApproved,
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.Adopt);
        using (lease)
            return await _adoptOp.ExecuteAsync(
                Guid.NewGuid(), parameters, inspection, legacyConsentApproved, progress, ct);
    }
    public async Task<SetupResult> RunSetupAsync(
        SetupRequest request,
        IProgress<SetupUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null)
            return new SetupResult(Guid.NewGuid(), OperationOutcome.Failure, FailureCode.Unknown, null,
                "Another operation is already in progress. Wait for it to complete.", []);
        using (lease)
            return await _setupOp.RunAsync(request, progress, ct);
    }

    public void KeepWaiting() => _setupOp.KeepWaiting();

    public void TryAgainStep() => _setupOp.TryAgainStep();

    public string ComposeSetupDetails() => _setupOp.ComposeDetails();

    public async Task<OperationResult> ResetAsync(
        IProgress<OperationUpdate> progress, CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.Reset);
        using (lease)
            return await _resetOp.ExecuteAsync(Guid.NewGuid(), progress, ct);
    }
    public async Task<OperationResult> VerifyPasswordAsync(
        string password,
        CancellationToken ct = default)
    {
        var lease = _lock.TryAcquire(TimeSpan.Zero);
        if (lease is null) return Busy(OperationKind.VerifyPassword);
        using (lease)
        {
            var state = _stateStore.Read();
            if (state.Health is not (HealthState.Running or HealthState.RunningUnhealthy) ||
                string.IsNullOrEmpty(state.LoopbackUrl))
            {
                return new OperationResult(Guid.NewGuid(), OperationKind.VerifyPassword, OperationOutcome.Failure,
                    "Appliance is not running.", null, FailureCode.Unknown);
            }

            if (!_certStore.IsInitialized()) _certStore.GenerateCertificates();
            using var clientCert = _certStore.LoadClientCert();
            using var caCert = _certStore.LoadCaCert();

            var cred = _healthCredentials.Retrieve();
            var siteName = cred?.SiteName ?? "site1.local";

            try
            {
                if (_healthCheckerFactory is not null)
                {
                    var checker = _healthCheckerFactory(siteName, state.LoopbackUrl, _healthCredentials);
                    return await VerifyPasswordWithCheckerAsync(checker, siteName, password, ct);
                }

                if (state.QgaPort is null)
                {
                    return new OperationResult(Guid.NewGuid(), OperationKind.VerifyPassword, OperationOutcome.Failure,
                        "Guest agent port not found.", null, FailureCode.Unknown);
                }

                var qga = await Protocols.Qga.QgaClient.ConnectAsync(
                    "127.0.0.1", state.QgaPort.Value, clientCert, caCert, ct);
                await using (qga)
                {
                    var guestOps = new Guest.GuestOperations(qga);
                    var checker = new Health.HealthChecker(
                        guestOps, siteName, state.LoopbackUrl, _healthCredentials);
                    return await VerifyPasswordWithCheckerAsync(checker, siteName, password, ct);
                }
            }
            catch (Exception ex)
            {
                return new OperationResult(Guid.NewGuid(), OperationKind.VerifyPassword, OperationOutcome.Failure,
                    $"Verification failed: {ex.Message}", null, FailureCode.Unknown);
            }
        }
    }

    private async Task<OperationResult> VerifyPasswordWithCheckerAsync(
        Health.HealthChecker checker, string siteName, string password, CancellationToken ct)
    {
        var (valid, rejected) = await checker.CheckPasswordAsync(password, ct);
        if (rejected || !valid)
        {
            return new OperationResult(Guid.NewGuid(), OperationKind.VerifyPassword, OperationOutcome.Failure,
                "ERPNext rejected the Administrator password.", null, FailureCode.PasswordRejected);
        }

        _healthCredentials.Store(siteName, password);

        var health = await checker.RunAsync(ct);
        if (!health.Healthy)
        {
            return new OperationResult(Guid.NewGuid(), OperationKind.VerifyPassword, OperationOutcome.Failure,
                $"Health check failed after password save: {health.Reason}", null,
                health.IsPasswordRejected ? FailureCode.PasswordRejected : FailureCode.Unknown);
        }

        _stateStore.Mutate(s =>
        {
            s.Health = HealthState.Running;
            s.HealthFailureReason = null;
            s.ActiveOperation = null;
        });

        return new OperationResult(Guid.NewGuid(), OperationKind.VerifyPassword, OperationOutcome.Success,
            "Password verified and saved.");
    }


    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A site-less accepted image may be rebuilt after a failed or abandoned init;
    /// once data.img is committed, replacement must go through Recover.
    /// </summary>
    public static bool CanBuildFrom(ApplianceState state, bool committedDataExists) =>
        (state.Readiness is ReadinessState.NotBuilt or ReadinessState.Built) &&
        string.IsNullOrEmpty(state.DataImagePath) &&
        !committedDataExists;

    private static bool HasCommittedDataOnDisk() =>
        File.Exists(Path.Combine(KnownPaths.ApplianceDir, "data.img")) ||
        File.Exists(Path.Combine(KnownPaths.ApplianceDir, ".data-committed"));

    private static OperationResult Busy(OperationKind kind) =>
        new(Guid.NewGuid(), kind, OperationOutcome.Failure,
            "Another operation is already in progress. Wait for it to complete.");
    public static bool CanBuildPreservingDataFrom(ApplianceState state, bool committedDataExists) =>
        committedDataExists || !string.IsNullOrEmpty(state.DataImagePath);

    private OperationResult InvalidReadiness(OperationKind kind, string required, string remediation) =>
        new(Guid.NewGuid(), kind, OperationOutcome.Failure,
            $"Cannot {kind.ToString().ToLowerInvariant()}: readiness must be {required}. {remediation}");

    public void Dispose() => _lock.Dispose();
}
