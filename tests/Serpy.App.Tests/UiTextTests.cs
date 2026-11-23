using System.Text.RegularExpressions;
using Serpy.App.Text;
using Serpy.Core.Contracts;
using Serpy.Core.Setup;

namespace Serpy.App.Tests;

public sealed class UiTextTests
{
    // AE23: none of these appears on a default screen.
    private static readonly string[] BannedTerms =
    [
        "appliance", "QEMU", "WHPX", "hypervisor", "QMP", "QGA", "cloud-init", "qcow2", ".img",
        "provision", "bench", "loopback", "port",
    ];

    private static bool Contains(string text, string term)
    {
        // A term made of letters is a whole word: "port" must not match "support" or "important".
        // A term with a dot or a hyphen is matched as written.
        var pattern = term.All(char.IsLetter)
            ? $@"\b{Regex.Escape(term)}s?\b"
            : Regex.Escape(term);
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
    }

    [Fact]
    public void NoDefaultScreenString_ContainsABannedTerm()
    {
        var offenders = UiText.DefaultScreenStrings
            .SelectMany(text => BannedTerms.Where(term => Contains(text, term)).Select(term => $"'{term}' in \"{text}\""))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheBannedTermCheck_CatchesAWordAndIgnoresItsLookalikes()
    {
        // Guards the check itself: a test that cannot fail proves nothing.
        Assert.True(Contains("Open the port", "port"));
        Assert.True(Contains("a qcow2 file", "qcow2"));
        Assert.True(Contains("data.img", ".img"));
        Assert.False(Contains("This is important and we support it", "port"));
    }

    [Fact]
    public void TheDefaultScreenList_IsFilledBeforeAnyScreenRuns()
    {
        // Static initializers record every string, so the check above sees all of them, not only the
        // strings that a screen happened to read first.
        Assert.True(UiText.DefaultScreenStrings.Count > 60);
        Assert.Contains("Serpy is ready", UiText.DefaultScreenStrings);
        Assert.Contains("{0} of {1} parts already on this PC", UiText.DefaultScreenStrings);
    }

    [Fact]
    public void EveryFailureCode_HasAHeadlineAndAWhatToDoText()
    {
        foreach (var code in Enum.GetValues<FailureCode>().Where(c => c != FailureCode.None))
        {
            Assert.Contains(code, UiText.FailureCodesWithText);
            var text = UiText.Failure(code);
            Assert.False(string.IsNullOrWhiteSpace(text.Headline), $"{code} has no headline");
            Assert.False(string.IsNullOrWhiteSpace(text.Detail), $"{code} has no what-happened text");
            Assert.False(string.IsNullOrWhiteSpace(text.WhatToDo), $"{code} has no what-to-do text");
        }
    }

    [Fact]
    public void ACodeWithNoTextOfItsOwn_ShowsTheGenericProblemText_NotABlankScreen()
    {
        var unknown = UiText.Failure((FailureCode)9999);

        Assert.Equal(UiText.Failure(FailureCode.Unknown), unknown);
    }

    [Fact]
    public void TheWelcomeSpaceLine_ShowsTheThresholdThePcCheckUses()
    {
        Assert.Equal("Needs about 30 GB of free space", UiText.WelcomeSpace(PcThresholds.Default));

        var changed = PcThresholds.Default with { MinFreeDiskBytes = 45L * 1024 * 1024 * 1024 };
        Assert.Equal("Needs about 45 GB of free space", UiText.WelcomeSpace(changed));
    }

    [Fact]
    public void TheSpaceFinding_NamesTheSpaceNeededAndTheSpaceFree()
    {
        const long Gb = 1024L * 1024 * 1024;

        Assert.Equal("Serpy needs about 30 GB. This PC has 11 GB free.", UiText.SpaceFinding(30 * Gb, 11 * Gb));
    }

    [Theory]
    [InlineData(SetupStepId.PcCheck, 1)]
    [InlineData(SetupStepId.Prepare, 1)]
    [InlineData(SetupStepId.Download, 2)]
    [InlineData(SetupStepId.Build, 3)]
    [InlineData(SetupStepId.CompanyData, 4)]
    [InlineData(SetupStepId.Start, 5)]
    public void EverySetupStep_LandsOnOneOfTheFiveStagesTheMockupsDraw(SetupStepId step, int position)
    {
        Assert.Equal(position, UiText.StripPosition(step));
        Assert.InRange(position, 1, UiText.StripSteps);
        Assert.False(string.IsNullOrWhiteSpace(UiText.StripLabel(position)));
    }

    [Fact]
    public void TheCheckList_NeverShowsCoresWordingForVirtualizationOrTheDownloadSites()
    {
        // Core words these lines for the details file: they name the hypervisor and the sites.
        var virtualization = new PcCheckLine(PcCheckId.Virtualization, PcCheckResult.Pass,
            "a hypervisor is running", "a hypervisor");
        var unreachable = new PcCheckLine(PcCheckId.Internet, PcCheckResult.Fail,
            "no answer from snapshot.debian.org, pypi.org", "every download site", FailureCode.NetworkUnavailable);

        foreach (var shown in new[] { UiText.CheckValue(virtualization), UiText.CheckValue(unreachable) })
        {
            Assert.DoesNotContain("hypervisor", shown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("debian", shown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".org", shown, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ANumericCheckLine_ShowsWhatWasMeasuredAndWhatIsNeeded()
    {
        var memory = new PcCheckLine(PcCheckId.Memory, PcCheckResult.Pass, "16.0 GB usable", "7.0 GB usable", FailureCode.PcCheckFinding);

        Assert.Equal("16.0 GB usable · needs 7.0 GB usable", UiText.CheckValue(memory));
    }

    [Fact]
    public void AFailureLine_ComposesLabelFoundAndNeeded_InPlainWords()
    {
        var memory = new PcCheckLine(PcCheckId.Memory, PcCheckResult.Fail, "6.0 GB usable", "7.0 GB usable", FailureCode.PcCheckFinding);

        Assert.Equal("Memory: 6.0 GB usable — needs 7.0 GB usable", UiText.CheckFinding(memory));
        Assert.DoesNotContain("hypervisor", UiText.CheckFinding(memory), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheReadyScreenCredentials_AreTheDocumentedDefaultsOfNewCompanyData()
    {
        // CompanyDataStep creates the site with this password. The Ready screen shows it, so the two
        // must agree, or a user would be told a password that does not work.
        Assert.Equal("Administrator", UiText.DefaultUsername);
        Assert.Equal("admin", UiText.DefaultPassword);
    }

    [Fact]
    public void TimeTexts_NeverShowZeroOrNegativeMinutesLeft_AndRoundToWholeMinutes()
    {
        Assert.Equal("about 1 min left", UiText.TimeLeft(TimeSpan.FromSeconds(10)));
        Assert.Equal("about 35 min left", UiText.TimeLeft(TimeSpan.FromMinutes(34.6)));
        Assert.Equal("12 min on this step", UiText.TimeOnStep(TimeSpan.FromMinutes(12.9)));
        Assert.Equal("0 min on this step", UiText.TimeOnStep(TimeSpan.FromSeconds(20)));
        Assert.Equal("Nothing new has happened for 10 minutes. It may still be working.",
            UiText.QuietBody(TimeSpan.FromMinutes(10)));
    }
}
