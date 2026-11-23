using System.Text;
using Serpy.Core.Contracts;

namespace Serpy.Core.Setup;

/// <summary>
/// Everything the PC check measures. The Windows probe fills it from the machine, and tests fill it
/// with values (KTD13). Every field is plain data so the evaluator below holds all the decisions.
/// </summary>
public sealed record PcFacts(
    int WindowsBuild,
    long TotalMemoryBytes,
    long FreeDiskBytes,
    string DataDrive,
    bool HypervisorRunning,
    bool WindowsFeatureInstalled,
    bool? FirmwareVirtualization,
    InternetFacts Internet);

/// <summary>What the internet probe found. NotNeeded means every file is already cached.</summary>
public sealed record InternetFacts(bool Needed, IReadOnlyList<HostReachability> Hosts)
{
    public static InternetFacts NotNeeded { get; } = new(false, []);
}

public sealed record HostReachability(string Host, bool Reachable);

/// <summary>The thresholds of KTD13. They live in one place, so U4's measured figures change one file.</summary>
/// <remarks>
/// MinWindowsBuild is 19044 (Windows 10 21H2, including Enterprise LTSC 2021). Windows Hypervisor
/// Platform ships in every Windows 10 2004 (19041) and later edition, so 19044 can always run the
/// appliance; 19045 (22H2) excluded machines that can never reach it.
/// MinMemoryBytes is a usable-memory floor that stands in for the 8 GB requirement: Windows reports
/// slightly less than the installed size (hardware/firmware reserve), so a genuine 8 GB PC reads
/// about 7.7–7.9 GiB usable. The floor tolerates that reserve and still fails 6 GB-class machines.
/// </remarks>
public sealed record PcThresholds(
    int MinWindowsBuild,
    long MinMemoryBytes,
    long MinFreeDiskBytes)
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>First values. U4's measured run sets the final ones.</summary>
    public static PcThresholds Default { get; } = new(
        MinWindowsBuild: 19044,
        MinMemoryBytes: 7 * Gb,
        MinFreeDiskBytes: 30 * Gb);
}

public enum PcCheckId
{
    WindowsVersion,
    Memory,
    DiskSpace,
    Virtualization,
    Internet,
}

public enum PcCheckResult
{
    Pass,
    /// <summary>Not a failure: Prepare can fix it, so setup goes on.</summary>
    NeedsPrepare,
    Fail,
}

/// <summary>One line of the report: what was measured, what is needed and how it came out.</summary>
public sealed record PcCheckLine(
    PcCheckId Check,
    PcCheckResult Result,
    string Measured,
    string Needed,
    FailureCode Code = FailureCode.None);

public sealed record PcCheckReport(IReadOnlyList<PcCheckLine> Lines)
{
    /// <summary>True when nothing blocks setup. A line that Prepare can fix does not block it.</summary>
    public bool CanRun => Lines.All(l => l.Result != PcCheckResult.Fail);

    /// <summary>True when Prepare must turn the Windows feature on before QEMU can run.</summary>
    public bool NeedsFeature => Lines.Any(l => l.Check == PcCheckId.Virtualization && l.Result == PcCheckResult.NeedsPrepare);

    /// <summary>The first blocking line, which decides the failure code and the screen.</summary>
    public PcCheckLine? FirstFinding => Lines.FirstOrDefault(l => l.Result == PcCheckResult.Fail);

    /// <summary>
    /// The lines a failure screen lists: the findings that block or need action, in report order.
    /// Lines Prepare can fix appear too, marked as handled; they are not failures.
    /// </summary>
    public IReadOnlyList<PcCheckLine> Findings =>
        Lines.Where(l => l.Result is PcCheckResult.Fail or PcCheckResult.NeedsPrepare).ToList();

    /// <summary>The text for the details file and the log.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        foreach (var line in Lines)
            text.AppendLine($"{line.Check,-15} {line.Result,-12} measured: {line.Measured}; needed: {line.Needed}");
        return text.ToString().TrimEnd();
    }
}

/// <summary>The decisions of the PC check. It reads facts and thresholds and nothing else.</summary>
public static class PcCheckEvaluator
{
    private const long Gb = 1024L * 1024 * 1024;

    public static PcCheckReport Evaluate(PcFacts facts, PcThresholds thresholds, long extraDiskBytes = 0)
    {
        var lines = new List<PcCheckLine>
        {
            Windows(facts, thresholds),
            Memory(facts, thresholds),
            Disk(facts, thresholds, extraDiskBytes),
            Virtualization(facts),
            Internet(facts),
        };
        return new PcCheckReport(lines);
    }

    private static PcCheckLine Windows(PcFacts facts, PcThresholds thresholds) =>
        new(PcCheckId.WindowsVersion,
            facts.WindowsBuild >= thresholds.MinWindowsBuild ? PcCheckResult.Pass : PcCheckResult.Fail,
            $"Windows {WindowsName(facts.WindowsBuild)} (build {facts.WindowsBuild})",
            $"Windows 10 or 11 (build {thresholds.MinWindowsBuild} or newer)",
            FailureCode.PcCheckFinding);

    // The build number decides the family; anything else reads as "an older Windows". The name is
    // the message for a non-technical user; the build stays for the person who helps them.
    private static string WindowsName(int build) => build switch
    {
        >= 22000 => "11",
        >= 10240 => "10",
        _ => "an older version of",
    };

    private static PcCheckLine Memory(PcFacts facts, PcThresholds thresholds) =>
        new(PcCheckId.Memory,
            facts.TotalMemoryBytes >= thresholds.MinMemoryBytes ? PcCheckResult.Pass : PcCheckResult.Fail,
            $"{Format(facts.TotalMemoryBytes)} usable", $"{Format(thresholds.MinMemoryBytes)} usable",
            FailureCode.PcCheckFinding);

    // Existing company data is copied for its trial, so its full size is needed on top (KTD11).
    private static PcCheckLine Disk(PcFacts facts, PcThresholds thresholds, long extraBytes)
    {
        var needed = thresholds.MinFreeDiskBytes + extraBytes;
        return new(PcCheckId.DiskSpace,
            facts.FreeDiskBytes >= needed ? PcCheckResult.Pass : PcCheckResult.Fail,
            $"{Format(facts.FreeDiskBytes)} free on {facts.DataDrive}", Format(needed),
            FailureCode.DiskFull);
    }

    // A running hypervisor passes whatever the firmware flag reads, because that flag reads false
    // under one. The flag counts only when no hypervisor runs (KTD13).
    private static PcCheckLine Virtualization(PcFacts facts)
    {
        if (facts.HypervisorRunning)
            return new(PcCheckId.Virtualization, PcCheckResult.Pass, "a hypervisor is running", "a hypervisor");

        if (facts.FirmwareVirtualization == true)
        {
            return facts.WindowsFeatureInstalled
                ? new(PcCheckId.Virtualization, PcCheckResult.Pass,
                    "virtualization is on and the Windows feature is installed", "virtualization")
                : new(PcCheckId.Virtualization, PcCheckResult.NeedsPrepare,
                    "virtualization is on; the Windows feature is off", "the Windows feature");
        }

        return new(PcCheckId.Virtualization, PcCheckResult.Fail,
            facts.FirmwareVirtualization is null
                ? "virtualization could not be read"
                : "virtualization is off in the firmware",
            "virtualization turned on in the firmware", FailureCode.PcCheckFinding);
    }

    private static PcCheckLine Internet(PcFacts facts)
    {
        if (!facts.Internet.Needed)
            return new(PcCheckId.Internet, PcCheckResult.Pass, "every file is already on this PC", "not needed");

        var unreachable = facts.Internet.Hosts.Where(h => !h.Reachable).Select(h => h.Host).ToList();
        return unreachable.Count == 0
            ? new(PcCheckId.Internet, PcCheckResult.Pass, "every download site answered", "every download site")
            : new(PcCheckId.Internet, PcCheckResult.Fail,
                $"no answer from {string.Join(", ", unreachable)}", "every download site",
                FailureCode.NetworkUnavailable);
    }
    // One decimal, so a figure never hides the gap to the needed one; and on a Fail the measured
    // figure rounds down, so the two can never look equal (a real 8 GB PC measured "8 GB; needed
    // 8 GB" before). "GB" names 1024-based bytes throughout this file.
    private static string Format(long bytes) => $"{bytes / (double)Gb:0.0} GB";
}
