namespace Serpy.Core.Setup;

/// <summary>
/// Reads the facts of this PC. No probe needs elevation (KTD13). The Windows implementation lives
/// beside the other Windows platform code; tests use a fake that returns values.
/// </summary>
public interface IPcProbe
{
    /// <param name="dataDirectory">A folder on the drive that will hold the appliance.</param>
    /// <param name="hostsToReach">Download hosts to send one request to. Empty when nothing needs downloading.</param>
    Task<PcFacts> ReadAsync(string dataDirectory, IReadOnlyList<Uri> hostsToReach, CancellationToken ct);
}
