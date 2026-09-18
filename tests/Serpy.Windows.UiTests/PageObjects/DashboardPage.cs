namespace Serpy.Windows.UiTests.PageObjects;

public sealed class DashboardPage(Window window, UIA3Automation automation, Application? app = null)
{
    private readonly Window _window = window;
    private readonly UIA3Automation _automation = automation;
    private readonly Application? _app = app;

    public Window Window => _window;

    public Label? StatusBadge =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("StatusBadge"))?.AsLabel();

    public Label? LoopbackUrlText =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("LoopbackUrlText"))?.AsLabel();

    public Label? StageText =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("StageText"))?.AsLabel();

    public ProgressBar? ProgressBar =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("ProgressBar"))?.AsProgressBar();

    public Button? PrimaryActionButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryActionButton"))?.AsButton();

    public Button? BuildButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("BuildButton"))?.AsButton();

    public Button? InitializeButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("InitializeButton"))?.AsButton();

    public Button? StopButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("StopButton"))?.AsButton();

    public Button? RestartButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("RestartButton"))?.AsButton();

    public Button? RecoverButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("RecoverButton"))?.AsButton();

    public Button? OpenErpNextButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("OpenErpNextButton"))?.AsButton();

    public Button? CancelButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("CancelButton"))?.AsButton();

    public CheckBox? StartAtSignInCheckbox =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("StartAtSignInCheckbox"))?.AsCheckBox();

    public AutomationElement? DetailsExpander =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("DetailsExpander")) ??
        _window.FindFirstDescendant(cf => cf.ByAutomationId("ExpanderHeader"));

    public Label? DetailLogText =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("DetailLogText"))?.AsLabel();

    public bool WaitForStatus(string expectedBadgeText, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var current = StatusBadge?.Text;
            if (string.Equals(current, expectedBadgeText, StringComparison.OrdinalIgnoreCase))
                return true;

            Thread.Sleep(200);
        }

        return false;
    }

    public InitializeDialogPage ClickInitialize(TimeSpan? timeout = null)
    {
        var btn = PrimaryActionButton ?? InitializeButton ?? throw new InvalidOperationException("InitializeButton not found.");
        try
        {
            _window.SetForeground();
            _window.Focus();
        }
        catch { /* best effort */ }

        btn.Focus();
        Thread.Sleep(200);

        try
        {
            btn.Invoke();
        }
        catch
        {
            try
            {
                btn.Click();
            }
            catch
            {
                Keyboard.Type(VirtualKeyShort.SPACE);
            }
        }

        var waitTime = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + waitTime;

        while (DateTime.UtcNow < deadline)
        {
            // 1. Direct child of main window
            var childModal = _window.FindFirstChild(cf => cf.ByControlType(ControlType.Window));
            if (childModal is not null)
                return new InitializeDialogPage(childModal.AsWindow());

            // 2. Any descendant of main window of type Window
            var descendantModal = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window));
            if (descendantModal is not null)
                return new InitializeDialogPage(descendantModal.AsWindow());

            // 3. ModalWindows collection
            var modalFromCol = _window.ModalWindows.FirstOrDefault();
            if (modalFromCol is not null)
                return new InitializeDialogPage(modalFromCol);

            // 4. Desktop top-level windows
            var desktopWindows = _automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window));
            foreach (var win in desktopWindows)
            {
                var title = win.Properties.Name.ValueOrDefault ?? "";
                if (title.Contains("Set Up", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Appliance", StringComparison.OrdinalIgnoreCase))
                {
                    return new InitializeDialogPage(win.AsWindow());
                }
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException("Timed out waiting for InitializeDialog to open.");
    }

    public ExitDialogPage RequestCloseWhileRunning(TimeSpan? timeout = null)
    {
        _window.Close();

        var waitTime = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + waitTime;

        while (DateTime.UtcNow < deadline)
        {
            var childModal = _window.FindFirstChild(cf => cf.ByControlType(ControlType.Window));
            if (childModal is not null)
                return new ExitDialogPage(childModal.AsWindow());

            var descendantModal = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window));
            if (descendantModal is not null)
                return new ExitDialogPage(descendantModal.AsWindow());

            var desktopWindows = _automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window));
            foreach (var win in desktopWindows)
            {
                var title = win.Properties.Name.ValueOrDefault ?? "";
                if (title.Contains("running", StringComparison.OrdinalIgnoreCase))
                {
                    return new ExitDialogPage(win.AsWindow());
                }
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException("Timed out waiting for ExitWhileRunningDialog to open.");
    }
}
