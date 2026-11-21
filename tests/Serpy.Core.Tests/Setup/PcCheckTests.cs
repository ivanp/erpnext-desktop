using Serpy.Core.Contracts;
using Serpy.Core.Setup;

namespace Serpy.Core.Tests.Setup;

public sealed class PcCheckTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static PcFacts Good(
        int build = 26200,
        long memory = 32 * Gb,
        long free = 300 * Gb,
        bool hypervisor = true,
        bool feature = true,
        bool? firmware = false,
        InternetFacts? internet = null) =>
        new(build, memory, free, @"C:\", hypervisor, feature, firmware, internet ?? InternetFacts.NotNeeded);

    private static PcCheckReport Run(PcFacts facts, long extra = 0) =>
        PcCheckEvaluator.Evaluate(facts, PcThresholds.Default, extra);

    private static PcCheckLine Line(PcCheckReport report, PcCheckId id) => report.Lines.Single(l => l.Check == id);

    private sealed class FakeProbe(PcFacts facts) : IPcProbe
    {
        public int Reads;
        public IReadOnlyList<Uri> LastHosts = [];

        public Task<PcFacts> ReadAsync(string dataDirectory, IReadOnlyList<Uri> hostsToReach, CancellationToken ct)
        {
            Reads++;
            LastHosts = hostsToReach;
            return Task.FromResult(facts);
        }
    }

    private static SetupContext NewContext() => new()
    {
        RunId = Guid.NewGuid(),
        JournalOpen = false,
        Activity = _ => { },
        StepPercent = _ => { },
        Log = _ => { },
    };

    // ── disk, memory, Windows ────────────────────────────────────────────────

    [Fact]
    public void FreeSpaceBelowTheThreshold_Fails_AndReportsTheSpaceNeededAndTheSpaceFree()
    {
        var report = Run(Good(free: 12 * Gb));

        var line = Line(report, PcCheckId.DiskSpace);
        Assert.Equal(PcCheckResult.Fail, line.Result);
        Assert.Equal(FailureCode.DiskFull, line.Code);
        Assert.Contains("12 GB free on C:", line.Measured, StringComparison.Ordinal);
        Assert.Equal("30 GB", line.Needed);
        Assert.False(report.CanRun);
    }

    [Fact]
    public void ExistingCompanyData_RaisesTheSpaceNeededByItsFullSize()
    {
        var withoutData = Run(Good(free: 60 * Gb));
        var withData = Run(Good(free: 60 * Gb), extra: 40 * Gb);

        Assert.Equal(PcCheckResult.Pass, Line(withoutData, PcCheckId.DiskSpace).Result);
        Assert.Equal(PcCheckResult.Fail, Line(withData, PcCheckId.DiskSpace).Result);
        Assert.Equal("70 GB", Line(withData, PcCheckId.DiskSpace).Needed);
    }

    [Fact]
    public void MemoryBelowTheThreshold_Fails_AndReportsBothFigures()
    {
        var line = Line(Run(Good(memory: 4 * Gb)), PcCheckId.Memory);

        Assert.Equal(PcCheckResult.Fail, line.Result);
        Assert.Equal("4 GB", line.Measured);
        Assert.Equal("8 GB", line.Needed);
    }

    [Fact]
    public void AWindowsBuildBelowTheThreshold_Fails()
    {
        var line = Line(Run(Good(build: 19044)), PcCheckId.WindowsVersion);

        Assert.Equal(PcCheckResult.Fail, line.Result);
        Assert.Contains("19044", line.Measured, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePcThatMeetsEveryThreshold_CanRun()
    {
        var report = Run(Good());

        Assert.True(report.CanRun);
        Assert.All(report.Lines, l => Assert.Equal(PcCheckResult.Pass, l.Result));
        Assert.Null(report.FirstFinding);
    }

    // ── virtualization ───────────────────────────────────────────────────────

    [Fact]
    public void NoHypervisorAndFirmwareVirtualizationOff_GiveTheFindingThatSerpyCannotFix()
    {
        var report = Run(Good(hypervisor: false, feature: false, firmware: false));

        var line = Line(report, PcCheckId.Virtualization);
        Assert.Equal(PcCheckResult.Fail, line.Result);
        Assert.Equal(FailureCode.PcCheckFinding, line.Code);
        Assert.Contains("off in the firmware", line.Measured, StringComparison.Ordinal);
        Assert.False(report.CanRun);
    }

    [Fact]
    public void ARunningHypervisor_Passes_WhateverTheFirmwareFlagReads()
    {
        // The flag reads false under a hypervisor. The reference PC measured exactly this.
        foreach (bool? firmware in new bool?[] { false, true, null })
        {
            var line = Line(Run(Good(hypervisor: true, firmware: firmware)), PcCheckId.Virtualization);
            Assert.Equal(PcCheckResult.Pass, line.Result);
        }
    }

    [Fact]
    public void AMissingWindowsFeatureWithFirmwareVirtualizationOn_Passes_AndMarksThatPrepareMustTurnItOn()
    {
        var report = Run(Good(hypervisor: false, feature: false, firmware: true));

        Assert.Equal(PcCheckResult.NeedsPrepare, Line(report, PcCheckId.Virtualization).Result);
        Assert.True(report.CanRun);
        Assert.True(report.NeedsFeature);
    }

    [Fact]
    public void FirmwareVirtualizationOnAndTheFeatureInstalled_Passes_WithNothingForPrepareToDo()
    {
        var report = Run(Good(hypervisor: false, feature: true, firmware: true));

        Assert.Equal(PcCheckResult.Pass, Line(report, PcCheckId.Virtualization).Result);
        Assert.False(report.NeedsFeature);
    }

    [Fact]
    public void AFirmwareFlagThatCannotBeRead_WithNoHypervisor_IsAFinding_NotAGuess()
    {
        var report = Run(Good(hypervisor: false, feature: false, firmware: null));

        var line = Line(report, PcCheckId.Virtualization);
        Assert.Equal(PcCheckResult.Fail, line.Result);
        Assert.Contains("could not be read", line.Measured, StringComparison.Ordinal);
    }

    // ── internet ─────────────────────────────────────────────────────────────

    [Fact]
    public void NoInternetWithFilesStillToDownload_GivesTheNetworkUnavailableCode()
    {
        var internet = new InternetFacts(true,
            [new("snapshot.debian.org", false), new("nodejs.org", true)]);

        var report = Run(Good(internet: internet));

        var line = Line(report, PcCheckId.Internet);
        Assert.Equal(PcCheckResult.Fail, line.Result);
        Assert.Equal(FailureCode.NetworkUnavailable, line.Code);
        Assert.Contains("snapshot.debian.org", line.Measured, StringComparison.Ordinal);
        Assert.DoesNotContain("nodejs.org", line.Measured, StringComparison.Ordinal);
    }

    [Fact]
    public void NoInternet_WithEveryFileAlreadyCached_Passes()
    {
        var report = Run(Good(internet: InternetFacts.NotNeeded));

        Assert.Equal(PcCheckResult.Pass, Line(report, PcCheckId.Internet).Result);
        Assert.True(report.CanRun);
    }

    [Fact]
    public void EveryDownloadSiteAnswering_Passes()
    {
        var report = Run(Good(internet: new InternetFacts(true, [new("a.example", true), new("b.example", true)])));

        Assert.Equal(PcCheckResult.Pass, Line(report, PcCheckId.Internet).Result);
    }

    // ── the step ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAgain_RunsEveryProbeAgain_BecauseTheStepNeverReadsAsDone()
    {
        var probe = new FakeProbe(Good());
        var step = new PcCheckStep(probe, () => @"C:\data", () => [new Uri("https://a.example/x")]);

        Assert.False(step.IsDone(NewContext()));
        await step.RunAsync(NewContext(), CancellationToken.None);
        Assert.False(step.IsDone(NewContext()));
        await step.RunAsync(NewContext(), CancellationToken.None);

        Assert.Equal(2, probe.Reads);
        Assert.Equal("https://a.example/x", probe.LastHosts.Single().ToString());
    }

    [Fact]
    public async Task AFailingPc_EndsTheStepWithTheFindingsCode_BeforeAnyDownloadCouldStart()
    {
        var step = new PcCheckStep(new FakeProbe(Good(free: 5 * Gb)), () => @"C:\data", () => []);
        var context = NewContext();

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.DiskFull, result.Code);
        Assert.Contains("DiskSpace", context.PcCheckReport, StringComparison.Ordinal);
        Assert.Contains("5 GB free on C:", context.PcCheckReport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APassingPc_PutsTheReportInTheContext_AndTellsPrepareWhetherTheFeatureIsNeeded()
    {
        var step = new PcCheckStep(
            new FakeProbe(Good(hypervisor: false, feature: false, firmware: true)), () => @"C:\data", () => []);
        var context = NewContext();

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.True(context.NeedsFeature);
        Assert.NotNull(context.PcCheckReport);
    }

    [Fact]
    public async Task TheExtraSpaceForExistingData_IsAskedForEachRun_NotCachedFromTheFirst()
    {
        long extra = 0;
        var step = new PcCheckStep(new FakeProbe(Good(free: 60 * Gb)), () => @"C:\data", () => [], () => extra);

        Assert.Equal(OperationOutcome.Success, (await step.RunAsync(NewContext(), CancellationToken.None)).Outcome);
        extra = 50 * Gb;
        Assert.Equal(OperationOutcome.Failure, (await step.RunAsync(NewContext(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public void TheReportText_NamesEachCheckWithItsMeasuredAndNeededValues()
    {
        var text = Run(Good(memory: 4 * Gb)).ToText();

        Assert.Contains("Memory", text, StringComparison.Ordinal);
        Assert.Contains("measured: 4 GB; needed: 8 GB", text, StringComparison.Ordinal);
        Assert.Equal(5, text.Split('\n').Length);
    }
}
