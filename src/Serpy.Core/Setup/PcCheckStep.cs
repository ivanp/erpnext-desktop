using Serpy.Core.Contracts;

namespace Serpy.Core.Setup;

/// <summary>
/// The first step of every run (KTD7): it probes the PC and ends the run before any large download
/// when the PC cannot run Serpy. It runs again on every run and on Check again, so it never reads as
/// done. It changes nothing on disk, so it has no leftovers.
/// </summary>
public sealed class PcCheckStep(
    IPcProbe probe,
    Func<string> dataDirectory,
    Func<IReadOnlyList<Uri>> hostsToReach,
    Func<long>? extraDiskBytes = null,
    PcThresholds? thresholds = null) : ISetupStep
{
    public SetupStepId Id => SetupStepId.PcCheck;
    public int Weight => 2;
    public TimeSpan QuietTime => TimeSpan.FromMinutes(2);

    public bool IsDone(SetupContext context) => false;

    public async Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        context.Activity("Checking this PC");
        var facts = await probe.ReadAsync(dataDirectory(), hostsToReach(), ct).ConfigureAwait(false);
        var report = PcCheckEvaluator.Evaluate(facts, thresholds ?? PcThresholds.Default, extraDiskBytes?.Invoke() ?? 0);

        context.PcCheckReport = report.ToText();
        context.PcCheckFindings = report.Findings;
        context.Log("PC check:\n" + context.PcCheckReport);
        context.NeedsFeature = report.NeedsFeature;

        if (report.CanRun) return SetupStepResult.Success("This PC can run Serpy.");

        var finding = report.FirstFinding!;
        return SetupStepResult.Failed(finding.Code,
            $"{finding.Check}: {finding.Measured}; needed {finding.Needed}");
    }

    public void DeleteLeftovers(SetupContext context) { }
}
