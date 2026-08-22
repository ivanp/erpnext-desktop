namespace Serpy.Core.Versions;

/// <summary>
/// R9 version gate: validates each guest runtime component against the manifest.
/// A lock mismatch or floor violation aborts the build and removes its partial image,
/// reporting component / installed / locked / floor for each failure.
/// </summary>
public static class VersionGate
{
    /// <summary>
    /// Validate all guest versions against the manifest.
    /// Throws <see cref="VersionGateException"/> on the first failure.
    /// </summary>
    public static void Validate(GuestVersionReport report, VersionManifest manifest)
    {
        var rt = manifest.Runtime;

        Check("python", report.Python, rt.Python.Lock, rt.Python.Floor);
        Check("nodejs", report.Node, rt.Node.Lock, rt.Node.Floor);
        Check("mariadb", report.MariaDb, rt.MariaDb.Lock, rt.MariaDb.Floor);
        Check("redis", report.Redis, rt.Redis.Lock, rt.Redis.Floor);
    }

    private static void Check(string component, string installed, string locked, string floor)
    {
        if (!VersionEvaluator.MatchesLock(installed, locked))
            throw new VersionGateException(component, installed, locked, floor,
                $"Version lock mismatch: installed={installed} locked={locked} floor={floor}");

        if (!VersionEvaluator.MeetsFloor(installed, floor))
            throw new VersionGateException(component, installed, locked, floor,
                $"Version below floor: installed={installed} locked={locked} floor={floor}");
    }
}

/// <summary>
/// Versions queried from the guest via QGA after provisioning.
/// </summary>
public sealed class GuestVersionReport
{
    public string Python { get; set; } = string.Empty;
    public string Node { get; set; } = string.Empty;
    public string MariaDb { get; set; } = string.Empty;
    public string Redis { get; set; } = string.Empty;
}

/// <summary>
/// Thrown when R9 validation fails; carries component/installed/locked/floor for the failing check.
/// </summary>
public sealed class VersionGateException(
    string component,
    string installed,
    string locked,
    string floor,
    string message) : Exception(message)
{
    public string Component { get; } = component;
    public string Installed { get; } = installed;
    public string Locked { get; } = locked;
    public string Floor { get; } = floor;
}
