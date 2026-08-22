using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Health;
using Serpy.Core.Images;
using Serpy.Core.Qemu;
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
    private readonly StatusOperation _statusOp;
    private readonly BuildOperation _buildOp;
    private readonly InitializeOperation _initOp;
    private readonly StartOperation _startOp;
    private readonly StopOperation _stopOp;
    private readonly RecoverOperation _recoverOp;

    public ApplianceService(
        ManagedRuntimeResolver runtimeResolver,
        TlsCertificateStore certStore,
        QemuImageTool imageTool,
        BaseImageDownloader imageDownloader,
        NoCloudSeedWriter seedWriter,
        HealthCredentials healthCredentials,
        StateStore stateStore,
        VersionManifest manifest,
        ApplianceSettings settings)
    {
        _lock      = new LifecycleLock();
        _statusOp  = new StatusOperation(stateStore);
        _buildOp   = new BuildOperation(
            imageDownloader, seedWriter, imageTool,
            runtimeResolver, certStore, manifest, settings);
        _initOp    = new InitializeOperation(
            imageTool, runtimeResolver, certStore,
            healthCredentials, stateStore, settings);
        _startOp   = new StartOperation(runtimeResolver, certStore, stateStore, settings);
        _stopOp    = new StopOperation(stateStore, certStore);
        _recoverOp = new RecoverOperation(
            imageTool, runtimeResolver, certStore,
            healthCredentials, stateStore, manifest, settings);
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
            return await _buildOp.ExecuteAsync(Guid.NewGuid(), progress, ct);
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
            return await _initOp.ExecuteAsync(
                Guid.NewGuid(), parameters.SiteName, parameters.AdminPassword, progress, ct);
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OperationResult Busy(OperationKind kind) =>
        new(Guid.NewGuid(), kind, OperationOutcome.Failure,
            "Another operation is already in progress. Wait for it to complete.");

    public void Dispose() => _lock.Dispose();
}
