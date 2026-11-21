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
public sealed record PcThresholds(
    int MinWindowsBuild,
    long MinMemoryBytes,
    long MinFreeDiskBytes)
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>First values. U4's measured run sets the final ones.</summary>
    public static PcThresholds Default { get; } = new(
        MinWindowsBuild: 19045,
        MinMemoryBytes: 8 * Gb,
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
            $"build {facts.WindowsBuild}", $"build {thresholds.MinWindowsBuild} or later",
            FailureCode.PcCheckFinding);

    private static PcCheckLine Memory(PcFacts facts, PcThresholds thresholds) =>
        new(PcCheckId.Memory,
            facts.TotalMemoryBytes >= thresholds.MinMemoryBytes ? PcCheckResult.Pass : PcCheckResult.Fail,
            Format(facts.TotalMemoryBytes), Format(thresholds.MinMemoryBytes),
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

    private static string Format(long bytes) => $"{bytes / (double)Gb:0.#} GB";
}
