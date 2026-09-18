namespace Serpy.Windows.UiTests.PageObjects;

public sealed class ExitDialogPage(Window window)
{
    private readonly Window _window = window;

    public Window Window => _window;

    public Button? StopApplianceButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("StopApplianceButton"))?.AsButton();

    public Button? LeaveRunningButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("LeaveRunningButton"))?.AsButton();

    public Button? CancelButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("CancelButton"))?.AsButton();

    public void ClickStop()
    {
        var btn = StopApplianceButton ?? throw new InvalidOperationException("StopApplianceButton not found.");
        btn.Click();
    }

    public void ClickLeaveRunning()
    {
        var btn = LeaveRunningButton ?? throw new InvalidOperationException("LeaveRunningButton not found.");
        btn.Click();
    }

    public void ClickCancel()
    {
        var btn = CancelButton ?? throw new InvalidOperationException("CancelButton not found.");
        btn.Click();
    }
}
