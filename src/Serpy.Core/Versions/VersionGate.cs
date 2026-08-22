namespace Serpy.Core.Versions;

/// <summary>
/// R9 version gate: validates each guest runtime component against the manifest.
/// A lock mismatch or floor violation aborts the build and removes its partial image,
/// reporting component / installed / locked / floor for each failure.
/// </summary>
public static class VersionGate
{
    /// <summary>
    /// Validate runtime versions (R9 — build gate).
    /// Checks Python, Node, MariaDB, Redis against exact locks and minimum floors.
    /// </summary>
    public static void Validate(GuestVersionReport report, VersionManifest manifest)
    {
        var rt   = manifest.Runtime;
        var apps = manifest.Apps;
        Check("python",  report.Python,  rt.Python.Lock,  rt.Python.Floor);
        Check("nodejs",  report.Node,    rt.Node.Lock,    rt.Node.Floor);
        Check("mariadb", report.MariaDb, rt.MariaDb.Lock, rt.MariaDb.Floor);
        Check("redis",   report.Redis,   rt.Redis.Lock,   rt.Redis.Floor);
        CheckApp("frappe",  report.Frappe,  apps.Frappe.Lock,  apps.Frappe.MinVersion);
        CheckApp("erpnext", report.ErpNext, apps.ErpNext.Lock, apps.ErpNext.MinVersion);
    }

    /// <summary>
    /// Validate that a replacement image does not downgrade Frappe or ERPNext (R6, KD5).
    /// Only checks floors (not locks) because replacements may carry patch updates.
    /// Throws <see cref="VersionGateException"/> if any app version is below its floor.
    /// </summary>
    public static void ValidateNoAppDowngrade(
        GuestVersionReport replacementReport, VersionManifest manifest)
    {
        var apps = manifest.Apps;
        CheckFloor("frappe",   replacementReport.Frappe,  apps.Frappe.MinVersion);
        CheckFloor("erpnext",  replacementReport.ErpNext, apps.ErpNext.MinVersion);
    }

    /// <summary>
    /// R6 replacement guard: every stateful component in a replacement system
    /// image must be equal to or newer than the currently installed image.
    /// </summary>
    public static void ValidateReplacementDoesNotDowngrade(
        GuestVersionReport replacementReport, GuestVersionReport installedReport)
    {
        CheckNotOlder("mariadb", replacementReport.MariaDb, installedReport.MariaDb);
        CheckNotOlder("frappe", replacementReport.Frappe, installedReport.Frappe);
        CheckNotOlder("erpnext", replacementReport.ErpNext, installedReport.ErpNext);
    }

    private static void CheckNotOlder(string component, string replacement, string installed)
    {
        if (string.IsNullOrWhiteSpace(replacement) || string.IsNullOrWhiteSpace(installed))
            throw new VersionGateException(component, replacement, installed, installed,
                $"Downgrade rejected: cannot compare replacement {component} with the installed image.");
        if (VersionEvaluator.Compare(replacement, installed) < 0)
            throw new VersionGateException(component, replacement, installed, installed,
                $"Downgrade rejected: replacement {component} {replacement} is below installed {installed}.");
    }

    private static void CheckFloor(string component, string installed, string floor)
    {
        if (string.IsNullOrEmpty(installed)) return;
        if (!VersionEvaluator.MeetsFloor(installed, floor))
            throw new VersionGateException(component, installed, floor, floor,
                $"Downgrade rejected: {component} {installed} is below floor {floor}");
    }

    private static void CheckApp(string component, string installed, string locked, string floor)
    {
        if (string.IsNullOrEmpty(installed))
            throw new VersionGateException(component, "(missing)", locked, floor,
                $"Version query returned no {component} version; build provisioning may have failed.");
        Check(component, installed, locked, floor);
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
    public string Python  { get; set; } = string.Empty;
    public string Node    { get; set; } = string.Empty;
    public string MariaDb { get; set; } = string.Empty;
    public string Redis   { get; set; } = string.Empty;
    /// <summary>Frappe version from `bench version`; set during build gate and recovery check.</summary>
    public string Frappe  { get; set; } = string.Empty;
    /// <summary>ERPNext version from `bench version`; set during build gate and recovery check.</summary>
    public string ErpNext { get; set; } = string.Empty;
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
