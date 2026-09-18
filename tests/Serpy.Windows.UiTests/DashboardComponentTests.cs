using Serpy.Windows.UiTests.Infrastructure;
using Serpy.Windows.UiTests.PageObjects;

namespace Serpy.Windows.UiTests;

[Collection("UiTests")]
public sealed class DashboardComponentTests
{
    [Fact]
    public void Dashboard_LaunchesCleanly_AndRendersAllCoreComponents()
    {
        using var fixture = new UiTestFixture();
        Application? app = null;
        Window? window = null;

        try
        {
            app = fixture.LaunchApp();
            window = fixture.GetMainWindow(TimeSpan.FromSeconds(10));
            Assert.NotNull(window);
            Assert.Equal("Serpy", window.Title);

            var dashboard = new DashboardPage(window, fixture.Automation, app);

            // 1. Status Badge
            Assert.NotNull(dashboard.StatusBadge);
            Assert.Equal("Not set up", dashboard.StatusBadge.Text);

            // 2. Buttons
            Assert.NotNull(dashboard.PrimaryActionButton);
            Assert.NotNull(dashboard.BuildButton);
            Assert.NotNull(dashboard.InitializeButton);
            Assert.NotNull(dashboard.StopButton);
            Assert.NotNull(dashboard.RestartButton);
            Assert.NotNull(dashboard.RecoverButton);
            Assert.NotNull(dashboard.OpenErpNextButton);
            Assert.NotNull(dashboard.CancelButton);

            // Initial enablement states when NotBuilt
            // PrimaryActionButton is enabled (launches Setup route / Build in NotBuilt state)
            Assert.True(dashboard.PrimaryActionButton.IsEnabled);
            Assert.True(dashboard.BuildButton.IsEnabled);
            Assert.False(dashboard.InitializeButton.IsEnabled);
            Assert.False(dashboard.StopButton.IsEnabled);
            Assert.False(dashboard.RestartButton.IsEnabled);
            Assert.False(dashboard.OpenErpNextButton.IsEnabled);
            Assert.False(dashboard.CancelButton.IsEnabled);

            // 3. Autostart checkbox
            Assert.NotNull(dashboard.StartAtSignInCheckbox);

            // 4. Details Expander
            Assert.NotNull(dashboard.DetailsExpander);
        }
        catch (Exception)
        {
            DiagnosticCapture.Capture(app, window, nameof(Dashboard_LaunchesCleanly_AndRendersAllCoreComponents), fixture.SandboxAppDataDir);
            throw;
        }
    }
}
