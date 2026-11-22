using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Serpy.App.ViewModels;
using Serpy.App.Views;
using Serpy.Core.Contracts;
using Serpy.Core.Downloads;
using Serpy.Core.Setup;
using Xunit;

namespace Serpy.App.Tests;

public sealed class LayoutInvariantTests
{
    private static SetupUpdate CreateUpdate(
        SetupRunState run,
        SetupStepId? current = null,
        bool stuck = false,
        FailureCode failure = FailureCode.None,
        IReadOnlyList<SetupStepStatus>? steps = null,
        int percent = 46,
        string activity = "adding ERPNext parts, 214 of 489",
        TimeSpan? elapsed = null,
        IReadOnlyList<PartDownloadSnapshot>? downloadParts = null) => new(
        Guid.NewGuid(), run, steps ?? (current.HasValue ? [new SetupStepStatus(current.Value, SetupStepState.Working)] : []), current, percent, activity,
        elapsed ?? TimeSpan.FromMinutes(12), stuck, failure, DownloadParts: downloadParts);

    private static T? FindByAutomationId<T>(Visual root, string automationId) where T : Visual =>
        root.GetVisualDescendants()
            .OfType<T>()
            .FirstOrDefault(v => AutomationProperties.GetAutomationId(v) == automationId);

    [AvaloniaFact]
    public void DashboardWindow_DefaultDimensions_MatchMockup()
    {
        var win = new DashboardWindow(false);
        Assert.Equal(560, win.Width);
        Assert.Equal(560, win.Height);
        Assert.Equal(480, win.MinWidth);
        Assert.Equal(440, win.MinHeight);
    }

    [AvaloniaFact]
    public void SetupView_WelcomeScreen_LayoutInvariants()
    {
        var vm = new DashboardViewModel();
        vm.Setup.Activate();
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var welcome = FindByAutomationId<StackPanel>(win, "WelcomeScreen");
        Assert.NotNull(welcome);
        Assert.True(welcome.IsVisible);

        var startBtn = FindByAutomationId<Button>(win, "StartSetupButton");
        Assert.NotNull(startBtn);
        Assert.True(startBtn.IsVisible);
        Assert.True(startBtn.Bounds.Right <= win.Bounds.Width);

        var check = FindByAutomationId<CheckBox>(win, "WelcomeStartAtSignInCheck");
        Assert.NotNull(check);
        Assert.True(check.IsVisible);
        Assert.True(check.IsChecked);

        var expander = FindByAutomationId<Expander>(win, "DetailsExpander");
        Assert.NotNull(expander);
        Assert.False(expander.IsExpanded);
        Assert.Equal("Show details", expander.Header?.ToString());

        win.Close();
    }

    [AvaloniaFact]
    public void SetupView_CheckingAndStepper_LayoutInvariants()
    {
        var vm = new DashboardViewModel();
        vm.Setup.Activate();
        vm.Setup.ApplySetupUpdate(CreateUpdate(SetupRunState.Working, SetupStepId.PcCheck));
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var checking = FindByAutomationId<StackPanel>(win, "CheckingScreen");
        Assert.NotNull(checking);
        Assert.True(checking.IsVisible);

        var stepper = FindByAutomationId<ItemsControl>(win, "StepperList");
        Assert.NotNull(stepper);
        Assert.True(stepper.IsVisible);

        for (var i = 1; i <= 5; i++)
        {
            var item = FindByAutomationId<TextBlock>(win, $"StripItem{i}");
            Assert.NotNull(item);
        }

        win.Close();
    }

    [AvaloniaFact]
    public void SetupView_Installing_NoDoubleNowPrefix()
    {
        var vm = new DashboardViewModel();
        vm.Setup.Activate();
        vm.Setup.ApplySetupUpdate(CreateUpdate(SetupRunState.Working, SetupStepId.Build));
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var activity = FindByAutomationId<TextBlock>(win, "ActivityText");
        Assert.NotNull(activity);
        Assert.False(string.IsNullOrWhiteSpace(activity.Text));
        Assert.DoesNotContain("Now: Now:", activity.Text);
        Assert.StartsWith("Now: ", activity.Text);

        win.Close();
    }

    [AvaloniaFact]
    public void SetupView_CannotRunAndProblem_HasBoldWhatToDo()
    {
        var vm = new DashboardViewModel();
        vm.Setup.Activate();
        vm.Setup.ApplySetupUpdate(CreateUpdate(SetupRunState.Failed, SetupStepId.PcCheck, failure: FailureCode.PcCheckFinding));
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var cannotRun = FindByAutomationId<TextBlock>(win, "CannotRunWhatToDoText");
        Assert.NotNull(cannotRun);
        Assert.True(cannotRun.Inlines?.Count > 0);
        Assert.Contains(cannotRun.Inlines!, inline => inline.FontWeight == FontWeight.Bold);

        win.Close();
    }

    [AvaloniaFact]
    public void SetupView_ReadyScreen_LayoutInvariants()
    {
        var vm = new DashboardViewModel();
        vm.Setup.Activate();
        vm.Setup.ApplyStatus(new ApplianceStatus(
            ReadinessState.Initialized,
            HealthState.Running,
            "http://127.0.0.1:18080",
            null,
            null,
            null,
            Setup: new SetupSummary(Finished: true, Working: false, FailureCode.None)));
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var ready = FindByAutomationId<StackPanel>(win, "ReadyScreen");
        Assert.NotNull(ready);
        Assert.True(ready.IsVisible);

        var openBtn = FindByAutomationId<Button>(win, "OpenErpNextButton");
        Assert.NotNull(openBtn);
        Assert.True(openBtn.IsVisible);

        win.Close();
    }

    [AvaloniaFact]
    public void SetupView_RestartScreen_HasBoldWhatToDo()
    {
        var vm = new DashboardViewModel();
        vm.Setup.Activate();
        vm.Setup.ApplySetupUpdate(CreateUpdate(SetupRunState.Failed, SetupStepId.PcCheck, failure: FailureCode.RestartRequired));
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var restartScreen = FindByAutomationId<StackPanel>(win, "RestartScreen");
        Assert.NotNull(restartScreen);
        var textBlocks = restartScreen.GetVisualDescendants().OfType<TextBlock>().ToArray();
        var adviceBlock = Assert.Single(textBlocks, tb => tb.Classes.Contains("hint"));
        Assert.True(adviceBlock.Inlines?.Count > 0);
        Assert.Contains(adviceBlock.Inlines!, inline => inline.FontWeight == FontWeight.Bold);

        win.Close();
    }

    [AvaloniaFact]
    public void SetupView_ProblemScreen_LayoutInvariants()
    {
        var vm = new DashboardViewModel();
        vm.Setup.Activate();
        vm.Setup.ApplySetupUpdate(CreateUpdate(
            SetupRunState.Failed,
            SetupStepId.Download,
            failure: FailureCode.NetworkUnavailable));
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var problem = FindByAutomationId<StackPanel>(win, "ProblemScreen");
        Assert.NotNull(problem);
        Assert.True(problem.IsVisible);

        var tryAgain = FindByAutomationId<Button>(win, "TryAgainButton");
        Assert.NotNull(tryAgain);
        Assert.True(tryAgain.IsVisible);

        var saveDetails = FindByAutomationId<Button>(win, "SaveDetailsButton");
        Assert.NotNull(saveDetails);
        Assert.True(saveDetails.IsVisible);

        var whatToDo = FindByAutomationId<TextBlock>(win, "FailureWhatToDoText");
        Assert.NotNull(whatToDo);
        Assert.True(whatToDo.Inlines?.Count > 0);
        Assert.Contains(whatToDo.Inlines!, inline => inline.FontWeight == FontWeight.Bold);

        win.Close();
    }

    [AvaloniaFact]
    public void EverydayView_RunningState_LayoutInvariants()
    {
        var vm = new DashboardViewModel
        {
            Status = new ApplianceStatus(
                ReadinessState.Initialized,
                HealthState.Running,
                "http://127.0.0.1:18080",
                null,
                null,
                null),
            ShowDetails = true,
            DetailLog = "Address: http://127.0.0.1:18080\nVersions: ERPNext 16.32.3",
        };
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var badge = FindByAutomationId<TextBlock>(win, "StatusBadge");
        Assert.NotNull(badge);
        Assert.Equal("Running", badge.Text);
        var badgeBorder = badge.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("badge"));
        Assert.NotNull(badgeBorder);
        Assert.Contains("ok", badgeBorder.Classes);
        Assert.Equal("ERPNext is running on this PC. It opens in your web browser.", vm.SubtitleText);
        Assert.Equal("http://127.0.0.1:18080 · works only on this PC", vm.AddressDisplayText);

        var openBtn = FindByAutomationId<Button>(win, "OpenErpNextButton");
        Assert.NotNull(openBtn);
        Assert.True(openBtn.IsVisible);
        Assert.True(openBtn.IsEnabled);

        var startStopBtn = FindByAutomationId<Button>(win, "StartOrStopButton");
        Assert.NotNull(startStopBtn);
        Assert.True(startStopBtn.IsVisible);
        Assert.True(startStopBtn.IsEnabled);
        Assert.Equal("Stop ERPNext", startStopBtn.Content?.ToString());

        var cancelBtn = FindByAutomationId<Button>(win, "CancelButton");
        Assert.NotNull(cancelBtn);

        var autostart = FindByAutomationId<CheckBox>(win, "StartAtSignInCheckbox");
        Assert.NotNull(autostart);
        Assert.True(autostart.IsVisible);

        var expander = FindByAutomationId<Expander>(win, "DetailsExpander");
        Assert.NotNull(expander);
        Assert.True(expander.IsExpanded);
        Assert.Equal("Hide details", expander.Header?.ToString());

        // In a 560px window, expander spans full panel width (> 450px, not clipped to ~350px)
        Assert.True(expander.Bounds.Width > 450, $"Expander width was {expander.Bounds.Width}, expected > 450px");

        // Header toggle presenter background stays transparent when expanded (no solid blue box)
        var headerToggle = expander.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.Name == "ExpanderHeader");
        Assert.NotNull(headerToggle);
        var togglePresenter = headerToggle.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "HeaderToggleContent");
        Assert.NotNull(togglePresenter);
        Assert.True(togglePresenter.Background == null ||
                    togglePresenter.Background == Brushes.Transparent ||
                    (togglePresenter.Background is ISolidColorBrush scb && scb.Color.A == 0));
        var restart = FindByAutomationId<Button>(win, "RestartButton");
        Assert.NotNull(restart);
        Assert.True(restart.IsEnabled);

        var recover = FindByAutomationId<Button>(win, "RecoverButton");
        Assert.NotNull(recover);

        var build = FindByAutomationId<Button>(win, "BuildButton");
        Assert.NotNull(build);

        var init = FindByAutomationId<Button>(win, "InitializeButton");
        Assert.NotNull(init);

        var log = FindByAutomationId<TextBlock>(win, "DetailLogText");
        Assert.NotNull(log);
        Assert.Contains("ERPNext 16.32.3", log.Text);

        win.Close();
    }

    [AvaloniaFact]
    public void EverydayView_NotBuiltState_LayoutInvariants()
    {
        var vm = new DashboardViewModel
        {
            Status = new ApplianceStatus(
                ReadinessState.NotBuilt,
                HealthState.Stopped,
                null,
                null,
                null,
                null,
                RecommendedRoute: LaunchRoute.Setup),
            ShowDetails = true,
        };
        vm.Setup.Deactivate();
        var win = new DashboardWindow(false) { DataContext = vm, Width = 560, Height = 560 };
        win.Show();

        var primaryAction = FindByAutomationId<Button>(win, "PrimaryActionButton");
        Assert.NotNull(primaryAction);
        Assert.True(primaryAction.IsVisible);
        Assert.True(primaryAction.IsEnabled);

        var build = FindByAutomationId<Button>(win, "BuildButton");
        Assert.NotNull(build);
        Assert.True(build.IsEnabled);

        var init = FindByAutomationId<Button>(win, "InitializeButton");
        Assert.NotNull(init);
        Assert.False(init.IsEnabled);

        win.Close();
    }
}
