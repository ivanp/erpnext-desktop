using Serpy.Windows.UiTests.Infrastructure;
using Serpy.Windows.UiTests.PageObjects;

namespace Serpy.Windows.UiTests;

/// <summary>
/// U9 component tests: the setup screens render and react to clicks.
/// Runs on the self-hosted WHPX runner (qemu-windows.yml); needs a real window.
/// </summary>
[Collection("UiTests")]
public sealed class SetupComponentTests
{
    [Fact]
    public void Setup_WelcomeScreen_ShowsStartControlsAndDetails()
    {
        using var fixture = new UiTestFixture();
        Application? app = null;
        Window? window = null;

        try
        {
            app = fixture.LaunchApp();
            window = fixture.GetMainWindow(TimeSpan.FromSeconds(10));
            Assert.NotNull(window);

            var setup = new SetupPage(window, fixture.Automation);

            // Fresh appliance routes to setup, which starts on the Welcome screen.
            Assert.True(setup.WaitForScreen("WelcomeScreen"));
            Assert.NotNull(setup.Host);
            Assert.NotNull(setup.StartSetupButton);
            Assert.NotNull(setup.WelcomeStartAtSignInCheck);
            Assert.True(setup.WelcomeStartAtSignInCheck.IsToggled);

            // Details live behind an expander that starts collapsed.
            Assert.NotNull(setup.DetailsExpander);
            Assert.Null(setup.DetailLogText);
        }
        catch (Exception)
        {
            DiagnosticCapture.Capture(app, window, nameof(Setup_WelcomeScreen_ShowsStartControlsAndDetails), fixture.SandboxAppDataDir);
            throw;
        }
    }

    [Fact]
    public void Setup_ExpandDetails_ShowsDetailLog()
    {
        using var fixture = new UiTestFixture();
        Application? app = null;
        Window? window = null;

        try
        {
            app = fixture.LaunchApp();
            window = fixture.GetMainWindow(TimeSpan.FromSeconds(10));
            Assert.NotNull(window);

            var setup = new SetupPage(window, fixture.Automation);
            Assert.True(setup.WaitForScreen("WelcomeScreen"));

            setup.ToggleDetails();

            // The detail log placeholder/log area exists once the expander is open.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && setup.DetailLogText is null)
            {
                Thread.Sleep(200);
            }

            Assert.NotNull(setup.DetailsExpander);
        }
        catch (Exception)
        {
            DiagnosticCapture.Capture(app, window, nameof(Setup_ExpandDetails_ShowsDetailLog), fixture.SandboxAppDataDir);
            throw;
        }
    }
}
