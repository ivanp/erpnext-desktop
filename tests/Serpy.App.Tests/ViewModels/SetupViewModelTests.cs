using Serpy.Core.Downloads;

using Serpy.App.Text;
using Serpy.App.ViewModels;
using Serpy.Core.Contracts;

namespace Serpy.App.Tests.ViewModels;

/// <summary>
/// U9: SetupViewModel is presentation-only. These tests pin the screen mapping,
/// the single RunSetupAsync contract, service forwarding and the details tail.
/// </summary>
public sealed class SetupViewModelTests
{
    private static SetupStepStatus[] NoSteps { get; } = [];

    private static SetupUpdate Update(
        SetupRunState run,
        SetupStepId? current = null,
        bool stuck = false,
        FailureCode failure = FailureCode.None,
        IReadOnlyList<SetupStepStatus>? steps = null) => new(
        Guid.NewGuid(), run, steps ?? NoSteps, current, 50, "doing things",
        TimeSpan.FromMinutes(3), stuck, failure);

    private static ApplianceStatus StatusWith(
        bool passwordRejected = false,
        SetupSummary? setup = null,
        string? loopbackUrl = null) => new(
        ReadinessState.Initialized, HealthState.Stopped, loopbackUrl, null, null, null,
        Setup: setup, PasswordRejected: passwordRejected);

    [Theory]
    [InlineData(SetupRunState.Working, SetupStepId.PcCheck, SetupScreen.Checking)]
    [InlineData(SetupRunState.Working, SetupStepId.Download, SetupScreen.Downloads)]
    [InlineData(SetupRunState.Working, SetupStepId.Build, SetupScreen.Installing)]
    [InlineData(SetupRunState.Working, SetupStepId.Start, SetupScreen.Installing)]
    [InlineData(SetupRunState.Failed, SetupStepId.PcCheck, SetupScreen.Problem)]
    [InlineData(SetupRunState.Cancelled, null, SetupScreen.Problem)]
    public void Update_RunAndStep_MapToExpectedScreen(SetupRunState run, SetupStepId? current, SetupScreen expected)
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        vm.ApplySetupUpdate(Update(run, current, failure: run == SetupRunState.Failed ? FailureCode.NetworkUnavailable : FailureCode.None));
        Assert.Equal(expected, vm.CurrentSetupScreen);
    }

    [Fact]
    public void Update_Failure_MapsToCannotRunRestartOrProblem()
    {
        var vm = new SetupViewModel(new FakeApplianceService());

        vm.ApplySetupUpdate(Update(SetupRunState.Failed, failure: FailureCode.RestartRequired));
        Assert.Equal(SetupScreen.RestartNeeded, vm.CurrentSetupScreen);

        vm.ApplySetupUpdate(Update(SetupRunState.Failed, failure: FailureCode.PcCheckFinding));
        Assert.Equal(SetupScreen.CannotRun, vm.CurrentSetupScreen);

        vm.ApplySetupUpdate(Update(SetupRunState.Failed, failure: FailureCode.DiskFull));
        Assert.Equal(SetupScreen.CannotRun, vm.CurrentSetupScreen);

        vm.ApplySetupUpdate(Update(SetupRunState.Failed, failure: FailureCode.ProvisioningFailed));
        Assert.Equal(SetupScreen.Problem, vm.CurrentSetupScreen);
    }

    [Fact]
    public void Update_Stuck_ShowsQuietBannerOnInstalling()
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        vm.ApplySetupUpdate(Update(SetupRunState.Working, SetupStepId.Build, stuck: true));
        Assert.Equal(SetupScreen.Installing, vm.CurrentSetupScreen);
        Assert.True(vm.Stuck);
        Assert.True(vm.ShowQuietBanner);
    }

    [Fact]
    public void Update_CarriesActivityElapsedAndPercent()
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        vm.ApplySetupUpdate(Update(SetupRunState.Working, SetupStepId.Build));
        Assert.Contains("doing things", vm.ActivityText);
        Assert.Equal(UiText.TimeOnStep(TimeSpan.FromMinutes(3)), vm.StepElapsedText);
        Assert.Equal(50, vm.OverallPercent);
    }

    [Theory]
    [InlineData(true, false, SetupScreen.PasswordNeeded)]
    [InlineData(false, true, SetupScreen.Ready)]
    [InlineData(false, false, SetupScreen.Welcome)]
    public void Status_PasswordRejectedWinsOverSummary(bool passwordRejected, bool finished, SetupScreen expected)
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        vm.ApplyStatus(StatusWith(
            passwordRejected: passwordRejected,
            setup: new SetupSummary(finished, Working: false, FailureCode.None)));
        Assert.Equal(expected, vm.CurrentSetupScreen);
    }

    [Fact]
    public void Status_WorkingSummary_ShowsInstalling()
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        vm.ApplyStatus(StatusWith(setup: new SetupSummary(Finished: false, Working: true, FailureCode.None)));
        Assert.Equal(SetupScreen.Installing, vm.CurrentSetupScreen);
    }

    [Fact]
    public void Status_CarriesLoopbackUrlForReadyScreen()
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        var urls = new List<string>();
        vm.OpenUrlRequested += (_, url) => urls.Add(url);

        vm.ApplyStatus(StatusWith(loopbackUrl: "http://127.0.0.1:8000/app"));
        vm.OpenErpNextCommand.Execute(null);

        var single = Assert.Single(urls);
        Assert.Equal("http://127.0.0.1:8000/app", single);
    }

    [Fact]
    public async Task StartSetup_SendsExactlyOneRequestWithStartAtSignIn()
    {
        var service = new FakeApplianceService();
        var vm = new SetupViewModel(service) { StartAtSignIn = false };

        await vm.StartSetupCommand.ExecuteAsync(null);

        var request = Assert.Single(service.SetupRequests);
        Assert.False(request.StartAtSignIn);
    }

    [Fact]
    public async Task StartSetup_RunsToReadyWithoutOpeningBrowser()
    {
        var service = new FakeApplianceService();
        var vm = new SetupViewModel(service);
        var browserCalls = 0;
        vm.OpenUrlRequested += (_, _) => browserCalls++;

        await vm.StartSetupCommand.ExecuteAsync(null);

        Assert.Equal(SetupScreen.Ready, vm.CurrentSetupScreen);
        Assert.Equal(0, browserCalls);
    }

    [Fact]
    public async Task SecondStartAfterFinishedRun_ResumesWithOneMoreRequest()
    {
        var service = new FakeApplianceService();
        var vm = new SetupViewModel(service);

        await vm.StartSetupCommand.ExecuteAsync(null);
        await vm.TryAgainCommand.ExecuteAsync(null);

        Assert.Equal(2, service.SetupRequests.Count);
    }

    [Fact]
    public void KeepWaiting_ForwardsToService()
    {
        var service = new FakeApplianceService();
        var vm = new SetupViewModel(service);
        var events = 0;
        vm.KeepWaitingRequested += (_, _) => events++;

        vm.KeepWaitingCommand.Execute(null);

        Assert.Equal(1, service.KeepWaitingCalls);
        Assert.Equal(1, events);
    }

    [Fact]
    public void TryAgainStep_ForwardsToService()
    {
        var service = new FakeApplianceService();
        var vm = new SetupViewModel(service);

        vm.TryAgainStepCommand.Execute(null);

        Assert.Equal(1, service.TryAgainCalls);
    }

    [Fact]
    public void Cancel_CancelsVmTokenOnly()
    {
        var service = new FakeApplianceService();
        var vm = new SetupViewModel(service);

        vm.CancelSetupCommand.Execute(null);

        Assert.Empty(service.SetupRequests);
    }

    [Fact]
    public async Task WrongPassword_StaysOnPasswordScreenWithoutRerun()
    {
        var service = new FakeApplianceService
        {
            CustomVerifyPasswordResult = _ => new OperationResult(
                Guid.NewGuid(), OperationKind.VerifyPassword, OperationOutcome.Failure,
                "ERPNext rejected the Administrator password.", null, FailureCode.PasswordRejected),
        };
        var vm = new SetupViewModel(service);
        vm.ApplyStatus(StatusWith(passwordRejected: true));
        vm.PasswordInput = "wrong";

        await vm.SubmitPasswordCommand.ExecuteAsync(null);

        Assert.Equal(SetupScreen.PasswordNeeded, vm.CurrentSetupScreen);
        Assert.True(vm.PasswordWrongVisible);
        Assert.Contains("wrong", service.VerifiedPasswords);
        Assert.Empty(service.SetupRequests);
    }

    [Fact]
    public async Task CorrectPassword_RerunsSetupToReady()
    {
        var service = new FakeApplianceService();
        var vm = new SetupViewModel(service);
        vm.ApplyStatus(StatusWith(passwordRejected: true));
        vm.PasswordInput = "admin";

        await vm.SubmitPasswordCommand.ExecuteAsync(null);

        Assert.Equal(SetupScreen.Ready, vm.CurrentSetupScreen);
        Assert.False(vm.PasswordWrongVisible);
        Assert.Single(service.SetupRequests);
        Assert.Contains("admin", service.VerifiedPasswords);
    }

    public static TheoryData<FailureCode> FailureCodes()
    {
        var data = new TheoryData<FailureCode>();
        foreach (var code in UiText.FailureCodesWithText)
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FailureCodes))]
    public void EveryFailureCode_ShowsHeadlineDetailWhatToDo(FailureCode code)
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        var expected = UiText.Failure(code);

        vm.ApplySetupUpdate(Update(SetupRunState.Failed, failure: code));

        Assert.Equal(expected.Headline, vm.FailureHeadline);
        Assert.Equal(expected.WhatToDo, vm.FailureWhatToDo);
        Assert.False(string.IsNullOrWhiteSpace(vm.FailureDetail));
        Assert.NotNull(vm.TryAgainCommand);
        Assert.NotNull(vm.SaveDetailsCommand);
    }

    [Fact]
    public void Strip_MapsEveryStepToItsPosition()
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        var steps = new[]
        {
            new SetupStepStatus(SetupStepId.PcCheck, SetupStepState.Done),
            new SetupStepStatus(SetupStepId.Prepare, SetupStepState.Done),
            new SetupStepStatus(SetupStepId.Download, SetupStepState.Working),
            new SetupStepStatus(SetupStepId.Build, SetupStepState.Pending),
            new SetupStepStatus(SetupStepId.CompanyData, SetupStepState.Pending),
            new SetupStepStatus(SetupStepId.Start, SetupStepState.Pending),
        };

        vm.ApplySetupUpdate(Update(SetupRunState.Working, SetupStepId.Download, steps: steps));

        Assert.Equal(5, vm.StripItems.Count);
        Assert.Equal(SetupStripState.Done, vm.StripItems[0].State);
        Assert.Equal(SetupStripState.Working, vm.StripItems[1].State);
        Assert.Equal(SetupStripState.Pending, vm.StripItems[4].State);
        Assert.Equal("…", vm.StripItems[1].StateText);
        Assert.Equal("✓", vm.StripItems[0].StateText);
        Assert.Equal(UiText.StripLabel(2), vm.StripItems[1].Label);
        Assert.Equal("StripItem2", vm.StripItems[1].AutomationId);
    }

    [Fact]
    public void Details_ShowPlaceholderWhenAbsent()
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        Assert.True(vm.DetailLogPlaceholderVisible);
        Assert.False(vm.DetailLogFilled);
        Assert.False(string.IsNullOrWhiteSpace(vm.DetailLogPlaceholder));
    }

    [Fact]
    public void Details_TailAppendsNewLogLines()
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        var path = Path.Combine(Path.GetTempPath(), $"serpy-u9-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path, "first line\n");
            vm.UseLogPath(path);
            vm.RefreshDetails();
            Assert.Contains("first line", vm.DetailLogText);

            File.AppendAllText(path, "second line\n");
            vm.RefreshDetails();
            Assert.Contains("second line", vm.DetailLogText);
            Assert.True(vm.DetailLogFilled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DownloadParts_MapsSnapshotsToPartViewModelsAndCachedLine()
    {
        var vm = new SetupViewModel(new FakeApplianceService());
        var snapshots = new PartDownloadSnapshot[]
        {
            new("engine", PartDownloadState.Done, 180_000_000, 180_000_000, 1, true),
            new("os", PartDownloadState.Done, 341_000_000, 341_000_000, 1, true),
            new("system-tools", PartDownloadState.Downloading, 100_000_000, 200_000_000, 212, false),
        };

        var update = new SetupUpdate(
            Guid.NewGuid(), SetupRunState.Working, NoSteps, SetupStepId.Download, 40, "Downloading system-tools",
            TimeSpan.FromMinutes(1), false, DownloadParts: snapshots);

        vm.ApplySetupUpdate(update);

        Assert.Equal(3, vm.DownloadParts.Count);
        var engine = vm.DownloadParts[0];
        Assert.Equal("Serpy engine", engine.Title);
        Assert.True(engine.IsDone);
        Assert.Contains(UiText.AlreadyOnThisPc, engine.StateText);

        var tools = vm.DownloadParts[2];
        Assert.Equal("System tools", tools.Title);
        Assert.Equal("212 files", tools.FileCountText);
        Assert.True(tools.ShowFileCount);
        Assert.Equal(50, tools.Percent);

        Assert.Equal(UiText.PartsCached(2, 3), vm.CachedPartsLine);
    }
}
