using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace Serpy.Windows.UiTests.PageObjects;

/// <summary>
/// Page object for the U9 setup screens. Only one screen is visible at a time,
/// so each lookup returns the first (and only) match of its automation ID.
/// </summary>
public sealed class SetupPage(Window window, UIA3Automation automation)
{
    private readonly Window _window = window;
    private readonly UIA3Automation _automation = automation;

    public AutomationElement? Host =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("SetupScreenHost"));

    public AutomationElement? WelcomeScreen =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("WelcomeScreen"));

    public Button? StartSetupButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("StartSetupButton"))?.AsButton();

    public CheckBox? WelcomeStartAtSignInCheck =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("WelcomeStartAtSignInCheck"))?.AsCheckBox();

    public AutomationElement? CheckingScreen =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("CheckingScreen"));

    public AutomationElement? StepperList =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("StepperList"));

    public AutomationElement? OverallProgressBar =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("OverallProgressBar"));

    public AutomationElement? QuietBanner =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("QuietBanner"));

    public Button? KeepWaitingButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("KeepWaitingButton"))?.AsButton();

    public Button? TryAgainStepButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("TryAgainStepButton"))?.AsButton();

    public Button? CancelSetupButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("CancelSetupButton"))?.AsButton();

    public AutomationElement? FailureHeadlineText =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("FailureHeadlineText"));

    public AutomationElement? FailureWhatToDoText =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("FailureWhatToDoText"));

    public Button? TryAgainButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("TryAgainButton"))?.AsButton();

    public Button? SaveDetailsButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("SaveDetailsButton"))?.AsButton();

    public AutomationElement? ReadyScreen =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("ReadyScreen"));

    public Button? OpenErpNextButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("OpenErpNextButton"))?.AsButton();

    public TextBox? PasswordInput =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("PasswordInput"))?.AsTextBox();

    public Button? PasswordSubmitButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("PasswordSubmitButton"))?.AsButton();

    public Button? PasswordCancelButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("PasswordCancelButton"))?.AsButton();

    public AutomationElement? DetailsExpander =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("DetailsExpander"));

    public Label? DetailLogText =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("DetailLogText"))?.AsLabel();

    /// <summary>
    /// Clicks a button with the Invoke → Click → Keyboard fallback from
    /// docs/solutions/ui-bugs/avalonia-uia-com-interop-missing-controls.md.
    /// </summary>
    public void Click(Button? button)
    {
        Assert.NotNull(button);
        try
        {
            _window.SetForeground();
            _window.Focus();
        }
        catch
        {
            // best effort
        }

        button.Focus();
        Thread.Sleep(200);

        try
        {
            button.Invoke();
        }
        catch
        {
            try
            {
                button.Click();
            }
            catch
            {
                Keyboard.Type(VirtualKeyShort.SPACE);
            }
        }
    }

    /// <summary>Expands or collapses the details expander via Invoke fallback.</summary>
    public void ToggleDetails()
    {
        var expander = DetailsExpander ?? throw new InvalidOperationException("DetailsExpander not found.");
        try
        {
            if (expander.Patterns.Invoke.IsSupported)
            {
                expander.Patterns.Invoke.Pattern.Invoke();
            }
            else if (expander.Patterns.ExpandCollapse.IsSupported)
            {
                expander.Patterns.ExpandCollapse.Pattern.Expand();
            }
            else
            {
                expander.Click();
            }
        }
        catch
        {
            expander.Click();
        }
    }

    public bool WaitForScreen(string automationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var screen = _window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (screen is not null)
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return false;
    }
}
