using Serpy.Windows.UiTests.Infrastructure;
using Serpy.Windows.UiTests.PageObjects;

namespace Serpy.Windows.UiTests;

[Collection("UiTests")]
public sealed class ApplianceLifecycleE2ETests
{
    [Fact]
    public void FastFixture_LifecycleWorkflow_InitializesStartsAndStopsAppliance()
    {
        using var fixture = new UiTestFixture();
        Application? app = null;
        Window? window = null;

        var defaultApplianceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Serpy", "appliance");
        var existingSystemQcow2 = Path.Combine(defaultApplianceDir, "system.qcow2");

        if (!File.Exists(existingSystemQcow2))
        {
            // Per Planning Contract: fast fixture requires pre-built system.qcow2 fixture;
            // skip gracefully if absent on runner rather than blocking or triggering 40-min build.
            return;
        }

        try
        {
            // Seed fixture with pre-built system.qcow2
            var sandboxAppliance = Path.Combine(fixture.SandboxAppDataDir, "appliance");
            File.Copy(existingSystemQcow2, Path.Combine(sandboxAppliance, "system.qcow2"), overwrite: true);

            // Copy manifest if exists
            var manifestSrc = Path.Combine(defaultApplianceDir, "system.manifest.json");
            if (File.Exists(manifestSrc))
            {
                File.Copy(manifestSrc, Path.Combine(sandboxAppliance, "system.manifest.json"), overwrite: true);
            }

            fixture.CopyRuntimeBundleFromDefaultIfAvailable();

            app = fixture.LaunchApp();
            window = fixture.GetMainWindow(TimeSpan.FromSeconds(10));
            var dashboard = new DashboardPage(window, fixture.Automation);

            // Verify Built state
            Assert.True(dashboard.WaitForStatus("Built", TimeSpan.FromSeconds(5)));

            // Open Initialize Dialog
            var initDialog = dashboard.ClickInitialize(TimeSpan.FromSeconds(5));
            initDialog.FillCredentials("erp.serpy.local", "admin", "admin");
            initDialog.ClickContinue();

            // Await Initialized status
            Assert.True(dashboard.WaitForStatus("Stopped", TimeSpan.FromSeconds(60)));

            // Click Start
            dashboard.PrimaryActionButton?.Click();

            // Await Running status
            Assert.True(dashboard.WaitForStatus("Running", TimeSpan.FromSeconds(60)));
            Assert.NotNull(dashboard.LoopbackUrlText);
            Assert.Contains("http://127.0.0.1:", dashboard.LoopbackUrlText.Text);

            // Click Stop
            dashboard.StopButton?.Click();
            Assert.True(dashboard.WaitForStatus("Stopped", TimeSpan.FromSeconds(60)));
        }
        catch (Exception)
        {
            DiagnosticCapture.Capture(app, window, nameof(FastFixture_LifecycleWorkflow_InitializesStartsAndStopsAppliance), fixture.SandboxAppDataDir);
            throw;
        }
    }

    [Fact]
    [Trait("Category", "LiveE2E")]
    public void Live_FullProvisioningWorkflow_FromScratchToHealthy()
    {
        // Opt-in full 30-45 minute live QEMU cloud-init build from scratch
        using var fixture = new UiTestFixture();
        Application? app = null;
        Window? window = null;

        try
        {
            app = fixture.LaunchApp();
            window = fixture.GetMainWindow(TimeSpan.FromSeconds(10));
            var dashboard = new DashboardPage(window, fixture.Automation);

            Assert.Equal("Not set up", dashboard.StatusBadge?.Text);

            // Start Build
            dashboard.BuildButton?.Click();

            // Await Built (30-60 min timeout for full live build)
            Assert.True(dashboard.WaitForStatus("Built", TimeSpan.FromMinutes(60)));

            // Initialize
            var initDialog = dashboard.ClickInitialize(TimeSpan.FromSeconds(5));
            initDialog.FillCredentials("erp.serpy.local", "admin", "admin");
            initDialog.ClickContinue();
            Assert.True(dashboard.WaitForStatus("Stopped", TimeSpan.FromMinutes(5)));

            // Start
            dashboard.PrimaryActionButton?.Click();
            Assert.True(dashboard.WaitForStatus("Running", TimeSpan.FromMinutes(5)));

            // Stop
            dashboard.StopButton?.Click();
            Assert.True(dashboard.WaitForStatus("Stopped", TimeSpan.FromMinutes(2)));
        }
        catch (Exception)
        {
            DiagnosticCapture.Capture(app, window, nameof(Live_FullProvisioningWorkflow_FromScratchToHealthy), fixture.SandboxAppDataDir);
            throw;
        }
    }
}
