using System.Net;
using System.Net.Sockets;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;
using Serpy.Core.Qemu;
using Serpy.Core.Health;
using Serpy.Core.Setup;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Operations;

[Collection("LifecycleLock")]
public sealed class PortProbeTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"SerpyPortTest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    private StateStore MakeStore(Action<ApplianceState>? init = null)
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        if (init is not null) store.Mutate(init);
        return store;
    }

    [Fact]
    public void FreePort_CanBind_ReturnsTrue()
    {
        var port = EphemeralPort.Allocate();
        Assert.True(PortProbe.CanBind(port));
    }

    [Fact]
    public void OccupiedPort_CanBind_ReturnsFalse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(PortProbe.CanBind(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void CheckErpNextPort_WhenOccupiedByForeignProcess_ReturnsPortInUseFailure()
    {
        var store = MakeStore();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = PortProbe.CheckErpNextPort(port, store, Guid.NewGuid(), OperationKind.Build);
            Assert.NotNull(result);
            Assert.Equal(OperationOutcome.Failure, result!.Outcome);
            Assert.Equal(FailureCode.PortInUse, result.Code);
            Assert.Contains($"Port {port} is already used", result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void CheckErpNextPort_WhenPortFree_ReturnsNull()
    {
        var store = MakeStore();
        var port = EphemeralPort.Allocate();
        var result = PortProbe.CheckErpNextPort(port, store, Guid.NewGuid(), OperationKind.Build);
        Assert.Null(result);
    }

    [Fact]
    public void CheckErpNextPort_WhenOurOwnApplianceIsRunning_DoesNotReportConflict()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Pretend our own current process is the running QEMU for this workspace
            using var currentProc = System.Diagnostics.Process.GetCurrentProcess();
            var currentPid = currentProc.Id;
            var currentStartTime = ProcessIdentity.StartTimeTicks(currentProc);

            var store = MakeStore(s =>
            {
                s.Health = HealthState.Running;
                s.QemuPid = currentPid;
                s.QemuStartTimeTicks = currentStartTime;
            });

            var result = PortProbe.CheckErpNextPort(port, store, Guid.NewGuid(), OperationKind.Start);
            Assert.Null(result);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task BuildStep_RunAsync_WhenPortOccupied_ReturnsFailedSetupStepResult()
    {
        var store = MakeStore();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var settings = new ApplianceSettings { ErpNextPort = port };
            var step = new BuildStep(
                build: null!,
                stateStore: store,
                applianceDir: () => _dir,
                settings: settings);

            var context = new SetupContext
            {
                RunId = Guid.NewGuid(),
                JournalOpen = false,
                Activity = _ => { },
                StepPercent = _ => { },
                Log = _ => { },
            };
            var result = await step.RunAsync(context, CancellationToken.None);

            Assert.Equal(OperationOutcome.Failure, result.Outcome);
            Assert.Equal(FailureCode.PortInUse, result.Code);
            Assert.Contains($"Port {port} is already used", result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void StartStep_Preflight_WhenPortOccupied_ReturnsFailedSetupStepResult()
    {
        var store = MakeStore();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var settings = new ApplianceSettings { ErpNextPort = port };
            var step = new StartStep(
                start: null!,
                stateStore: store,
                settings: settings);

            var context = new SetupContext
            {
                RunId = Guid.NewGuid(),
                JournalOpen = false,
                Activity = _ => { },
                StepPercent = _ => { },
                Log = _ => { },
            };
            var preflight = step.Preflight(context);

            Assert.NotNull(preflight);
            Assert.Equal(OperationOutcome.Failure, preflight!.Outcome);
            Assert.Equal(FailureCode.PortInUse, preflight.Code);
            Assert.Contains($"Port {port} is already used", preflight.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task InitializeOperation_ExecuteAsync_WhenPortOccupied_ReturnsPortInUseFailure()
    {
        var store = MakeStore();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var settings = new ApplianceSettings { ErpNextPort = port };
            var op = new InitializeOperation(
                imageTool: null!,
                runtimeResolver: null!,
                certStore: null!,
                healthCredentials: null!,
                stateStore: store,
                settings: settings);

            var result = await op.ExecuteAsync(Guid.NewGuid(), "site.local", "secret", new Progress<OperationUpdate>(), CancellationToken.None);

            Assert.Equal(OperationOutcome.Failure, result.Outcome);
            Assert.Equal(FailureCode.PortInUse, result.Code);
            Assert.Contains($"Port {port} is already used", result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RecoverOperation_ExecuteAsync_WhenPortOccupied_ReturnsPortInUseFailure()
    {
        var store = MakeStore();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var settings = new ApplianceSettings { ErpNextPort = port };
            var op = new RecoverOperation(
                imageTool: null!,
                runtimeResolver: null!,
                certStore: null!,
                healthCredentials: null!,
                stateStore: store,
                manifest: new VersionManifest(),
                settings: settings);

            var dummyPath = Path.Combine(_dir, "replacement.qcow2");
            File.WriteAllText(dummyPath, "dummy");
            var result = await op.ExecuteAsync(Guid.NewGuid(), dummyPath, new Progress<OperationUpdate>(), CancellationToken.None);

            Assert.Equal(OperationOutcome.Failure, result.Outcome);
            Assert.Equal(FailureCode.PortInUse, result.Code);
            Assert.Contains($"Port {port} is already used", result.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AdoptOperation_ExecuteAsync_WhenPortOccupied_ReturnsPortInUseFailure()
    {
        var prevAppdata = Environment.GetEnvironmentVariable("SERPY_TEST_APPDATA");
        try
        {
            Environment.SetEnvironmentVariable("SERPY_TEST_APPDATA", _dir);
            var applianceDir = KnownPaths.ApplianceDir;
            Directory.CreateDirectory(applianceDir);
            var archivePath = Path.Combine(applianceDir, "data.img");
            File.WriteAllBytes(archivePath, new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(applianceDir, "system.qcow2"), "system");

            var store = MakeStore();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var settings = new ApplianceSettings { ErpNextPort = port };
                var op = new AdoptOperation(
                    imageTool: null!,
                    runtimeResolver: null!,
                    certStore: null!,
                    healthCredentials: new HealthCredentials(Path.Combine(_dir, "creds")),
                    stateStore: store,
                    manifest: new VersionManifest(),
                    settings: settings);

                var inspection = new ArchiveInspection(
                    ArchiveInspectionVerdict.AuthenticatedPreflightPassed,
                    archivePath);
                var parameters = new AdoptParameters(archivePath, "admin");

                var result = await op.ExecuteAsync(
                    Guid.NewGuid(), parameters, inspection, legacyConsentApproved: true,
                    new Progress<OperationUpdate>(), CancellationToken.None);

                Assert.Equal(OperationOutcome.Failure, result.Outcome);
                Assert.Equal(FailureCode.PortInUse, result.Code);
                Assert.Contains($"Port {port} is already used", result.Message);
                Assert.False(File.Exists(Path.Combine(applianceDir, ".pending-adopted-data.img")));
            }
            finally
            {
                listener.Stop();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SERPY_TEST_APPDATA", prevAppdata);
        }
    }
}
