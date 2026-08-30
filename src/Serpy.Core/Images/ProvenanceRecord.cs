using Serpy.Core.Versions;

namespace Serpy.Core.Images;

/// <summary>
/// Authenticated provenance record stored alongside app-created datasets
/// (filename: {archivePath}.provenance). On Windows, this JSON payload
/// is encrypted with DPAPI (CurrentUser scope).
/// </summary>
public sealed record ProvenanceRecord
{
    public required string ArchiveSha256 { get; init; }
    public required DateTimeOffset CreatedTimestamp { get; init; }
    public required GuestVersionReport GuestVersions { get; init; }
}
