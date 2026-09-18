namespace Serpy.Windows.UiTests.PageObjects;

public sealed class InitializeDialogPage(Window window)
{
    private readonly Window _window = window;

    public Window Window => _window;

    public RadioButton? CreateNewRadio =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("CreateNewRadio"))?.AsRadioButton();

    public RadioButton? LoadExistingRadio =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("LoadExistingRadio"))?.AsRadioButton();

    public TextBox? SiteNameBox =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("SiteNameBox"))?.AsTextBox();

    public TextBox? ArchivePathBox =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("ArchivePathBox"))?.AsTextBox();

    public Button? BrowseArchiveButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("BrowseArchiveButton"))?.AsButton();

    public TextBox? PasswordBox =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox"))?.AsTextBox();

    public TextBox? ConfirmPasswordBox =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("ConfirmPasswordBox"))?.AsTextBox();

    public Label? ValidationMessageText =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("ValidationMessageText"))?.AsLabel();

    public Button? ContinueButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("ContinueButton"))?.AsButton();

    public Button? CancelButton =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId("CancelButton"))?.AsButton();

    public void FillCredentials(string siteName, string password, string confirmPassword)
    {
        if (SiteNameBox is { } siteBox)
        {
            siteBox.Text = siteName;
        }

        if (PasswordBox is { } pwdBox)
        {
            pwdBox.Text = password;
        }

        if (ConfirmPasswordBox is { } confBox)
        {
            confBox.Text = confirmPassword;
        }
    }

    public void ClickContinue()
    {
        var btn = ContinueButton ?? throw new InvalidOperationException("ContinueButton not found.");
        btn.Click();
    }

    public void ClickCancel()
    {
        var btn = CancelButton ?? throw new InvalidOperationException("CancelButton not found.");
        btn.Click();
    }
}
