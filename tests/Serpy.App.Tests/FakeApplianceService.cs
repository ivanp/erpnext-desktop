using Serpy.Core.Contracts;

namespace Serpy.App.Tests;

/// <summary>
/// Controllable fake IApplianceService for UI tests.
/// </summary>
public sealed class FakeApplianceService : IApplianceService
{
    public ApplianceStatus Status { get; set; } = new(
        ReadinessState.Initialized, HealthState.Stopped, null, null, null, null);

    public List<OperationKind> CalledOperations { get; } = [];
    public string? RecoveryImagePath { get; private set; }
    public ArchiveInspection? CustomInspectionResult { get; set; }

    public Task<OperationResult> BuildAsync(IProgress<OperationUpdate> p, CancellationToken ct = default)
    { CalledOperations.Add(OperationKind.Build); return Task.FromResult(Ok(OperationKind.Build)); }

    public Task<OperationResult> BuildPreservingDataAsync(IProgress<OperationUpdate> p, CancellationToken ct = default)
    { CalledOperations.Add(OperationKind.BuildPreservingData); return Task.FromResult(Ok(OperationKind.BuildPreservingData)); }

    public Task<OperationResult> InitializeAsync(
        InitializationParameters parameters,
        IProgress<OperationUpdate> p, CancellationToken ct = default)
    { CalledOperations.Add(OperationKind.Initialize); return Task.FromResult(Ok(OperationKind.Initialize)); }

    public Task<OperationResult> StartAsync(IProgress<OperationUpdate> p, CancellationToken ct = default)
    { CalledOperations.Add(OperationKind.Start); return Task.FromResult(Ok(OperationKind.Start)); }

    public Task<OperationResult> StopAsync(IProgress<OperationUpdate> p, CancellationToken ct = default)
    { CalledOperations.Add(OperationKind.Stop); return Task.FromResult(Ok(OperationKind.Stop)); }

    public Task<OperationResult> RestartAsync(IProgress<OperationUpdate> p, CancellationToken ct = default)
    { CalledOperations.Add(OperationKind.Restart); return Task.FromResult(Ok(OperationKind.Restart)); }

    public Task<OperationResult> RecoverAsync(string path, IProgress<OperationUpdate> p, CancellationToken ct = default)
    {
        RecoveryImagePath = path;
        CalledOperations.Add(OperationKind.Recover);
        return Task.FromResult(Ok(OperationKind.Recover));
    }

    public Task<ArchiveInspection> InspectArchiveAsync(string archivePath, CancellationToken ct = default)
    {
        CalledOperations.Add(OperationKind.InspectArchive);
        var result = CustomInspectionResult ?? new ArchiveInspection(
            ArchiveInspectionVerdict.AuthenticatedPreflightPassed,
            archivePath,
            "fake-digest");
        return Task.FromResult(result);
    }

    public Task<OperationResult> AdoptAsync(
        AdoptParameters parameters,
        ArchiveInspection inspection,
        bool legacyConsentApproved,
        IProgress<OperationUpdate> p, CancellationToken ct = default)
    {
        CalledOperations.Add(OperationKind.Adopt);
        return Task.FromResult(Ok(OperationKind.Adopt));
    }

    public Task<ApplianceStatus> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(Status);

    private static OperationResult Ok(OperationKind kind) =>
        new(Guid.NewGuid(), kind, OperationOutcome.Success, "OK");
}
