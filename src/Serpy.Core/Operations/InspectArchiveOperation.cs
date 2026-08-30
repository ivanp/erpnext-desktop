using Serpy.Core.Contracts;
using Serpy.Core.Images;
using Serpy.Core.Versions;

namespace Serpy.Core.Operations;

/// <summary>
/// Implements InspectArchiveAsync (KTD4, R7).
/// Performs a fast, non-mutating inspection of the archive's DPAPI provenance record metadata.
/// Never reads or streams the large archive file body.
/// </summary>
public sealed class InspectArchiveOperation(VersionManifest manifest)
{
    public Task<ArchiveInspection> ExecuteAsync(string archivePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return Task.FromResult(new ArchiveInspection(
                ArchiveInspectionVerdict.Rejected,
                archivePath ?? string.Empty,
                ExpectedDigest: null,
                ProvenanceJson: null,
                RejectionReason: "Archive file does not exist."));
        }

        string canonicalPath = Path.GetFullPath(archivePath);
        var record = ProvenanceRecordStore.Read(canonicalPath);

        if (record is null)
        {
            // Unlabeled legacy data — requires explicit user consent (R7)
            return Task.FromResult(new ArchiveInspection(
                ArchiveInspectionVerdict.LegacyConsentRequired,
                canonicalPath,
                ExpectedDigest: null,
                ProvenanceJson: null,
                RejectionReason: null));
        }

        // Authenticated DPAPI provenance record exists — run VersionGate preflight (R7)
        try
        {
            VersionGate.ValidateNoAppDowngrade(record.GuestVersions, manifest);
            return Task.FromResult(new ArchiveInspection(
                ArchiveInspectionVerdict.AuthenticatedPreflightPassed,
                canonicalPath,
                ExpectedDigest: record.ArchiveSha256,
                ProvenanceJson: ProvenanceRecordStore.GetProvenancePath(canonicalPath),
                RejectionReason: null));
        }
        catch (VersionGateException ex)
        {
            return Task.FromResult(new ArchiveInspection(
                ArchiveInspectionVerdict.Rejected,
                canonicalPath,
                ExpectedDigest: record.ArchiveSha256,
                ProvenanceJson: ProvenanceRecordStore.GetProvenancePath(canonicalPath),
                RejectionReason: $"Incompatible dataset version: {ex.Message}"));
        }
    }
}
