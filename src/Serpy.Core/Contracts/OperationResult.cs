namespace Serpy.Core.Contracts;

public enum OperationOutcome { Success, Failure, Cancelled }
public enum UpdateSeverity { Info, Warning, Error }

/// <summary>
/// Streamed progress event: GUI binds Message and optional log detail behind Show-details.
/// </summary>
public sealed record OperationUpdate(
    Guid OperationId,
    OperationKind Kind,
    string Stage,
    UpdateSeverity Severity,
    string Message,
    int? ProgressPercent,
    string? LogDetail);

/// <summary>
/// Terminal result returned from IApplianceService operations.
/// </summary>
public sealed record OperationResult(
    Guid OperationId,
    OperationKind Kind,
    OperationOutcome Outcome,
    string Message,
    string? LogPath = null);
