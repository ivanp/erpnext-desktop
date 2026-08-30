namespace Serpy.Core.Contracts;

public enum ArchiveInspectionVerdict
{
    AuthenticatedPreflightPassed = 0,
    LegacyConsentRequired = 1,
    Rejected = 2,
}

public sealed record ArchiveInspection(
    ArchiveInspectionVerdict Verdict,
    string CanonicalPath,
    string? ExpectedDigest = null,
    string? ProvenanceJson = null,
    string? RejectionReason = null);

public sealed record AdoptParameters(
    string ArchivePath,
    string AdminPassword);
public abstract record SetupChoice
{
    public sealed record CreateNew(InitializationParameters Parameters) : SetupChoice;
    public sealed record AdoptExisting(string ArchivePath, string AdminPassword) : SetupChoice;
}
