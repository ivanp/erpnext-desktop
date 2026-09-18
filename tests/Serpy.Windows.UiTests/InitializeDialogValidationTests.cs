using Serpy.Windows.UiTests.Infrastructure;
using Serpy.Windows.UiTests.PageObjects;

namespace Serpy.Windows.UiTests;

[Collection("UiTests")]
public sealed class InitializeDialogValidationTests
{
    [Fact]
    public void InitializeDialog_Validation_EnforcesPasswordsAndSiteName()
    {
        using var fixture = new UiTestFixture();

        // Seed state as Built so Initialize button is active
        var settingsDir = Path.Combine(fixture.SandboxAppDataDir, "settings");
        Directory.CreateDirectory(settingsDir);
        var stateStore = new Serpy.Core.Coordination.StateStore(Path.Combine(settingsDir, "state.json"));
        stateStore.Write(new Serpy.Core.Coordination.ApplianceState
        {
            Readiness = Serpy.Core.Contracts.ReadinessState.Built,
            Health = Serpy.Core.Contracts.HealthState.Stopped,
        });

        Application? app = null;
        Window? window = null;

        try
        {
            app = fixture.LaunchApp();
            window = fixture.GetMainWindow(TimeSpan.FromSeconds(10));
            var dashboard = new DashboardPage(window, fixture.Automation, app);
            Assert.True(dashboard.WaitForStatus("Setup incomplete", TimeSpan.FromSeconds(10)));

            // Open Initialize Dialog
            var dialog = dashboard.ClickInitialize(TimeSpan.FromSeconds(5));
            Assert.NotNull(dialog);
            Assert.Equal("Set Up ERPNext Appliance", dialog.Window.Title);

            // 1. Validate empty password
            dialog.FillCredentials("site1.local", "", "");
            dialog.ClickContinue();
            Thread.Sleep(300);

            Assert.NotNull(dialog.ValidationMessageText);
            Assert.Contains("password is required", dialog.ValidationMessageText.Text, StringComparison.OrdinalIgnoreCase);

            // 2. Validate mismatched passwords
            dialog.FillCredentials("site1.local", "secret123", "mismatch");
            dialog.ClickContinue();
            Thread.Sleep(300);

            Assert.Contains("do not match", dialog.ValidationMessageText.Text, StringComparison.OrdinalIgnoreCase);

            // 3. Validate invalid site name
            dialog.FillCredentials("INVALID_SITE!", "admin", "admin");
            dialog.ClickContinue();
            Thread.Sleep(300);

            Assert.Contains("domain name", dialog.ValidationMessageText.Text, StringComparison.OrdinalIgnoreCase);

            // 4. Cancel dismisses dialog
            dialog.ClickCancel();
            Thread.Sleep(500);

            // Dashboard window is still active
            Assert.NotNull(dashboard.Window);
            Assert.True(dashboard.Window.IsAvailable);
        }
        catch (Exception)
        {
            DiagnosticCapture.Capture(app, window, nameof(InitializeDialog_Validation_EnforcesPasswordsAndSiteName), fixture.SandboxAppDataDir);
            throw;
        }
    }
}
