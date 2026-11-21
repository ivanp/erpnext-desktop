using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;
using Serpy.Core.Configuration;
using Serpy.Core.Health;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;

/// <summary>
/// ApplianceService contract tests using a stub store — no QEMU.
/// Focuses on: lifecycle lock busy-detection, status non-mutating, state round-trip.
/// </summary>
[Collection("LifecycleLock")]
public sealed class ApplianceServiceContractTests : IDisposable
{
    // RecordAcceptedBuild and the status read look for committed data in the app-data folder. They
    // use a temporary root, so a data.img from a real setup cannot change what they expect.
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"SerpySvcTest-{Guid.NewGuid():N}");

    public ApplianceServiceContractTests() =>
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_dir, "appdata"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private StateStore MakeStore(Action<ApplianceState>? init = null)
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        if (init is not null) store.Mutate(init);
        return store;
    }

    // ── StatusOperation: non-mutating, stale PID detection ───────────────

    [Fact]
    public async Task GetStatus_NotBuilt_ReturnsNotBuiltStopped()
    {
        var store  = MakeStore();
        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(ReadinessState.NotBuilt, status.Readiness);
        Assert.Equal(HealthState.Stopped,     status.Health);
    }

    [Fact]
    public async Task GetStatus_Initialized_ReturnsInitializedStopped()
    {
        var store = MakeStore(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.Health    = HealthState.Stopped;
        });

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(ReadinessState.Initialized, status.Readiness);
        Assert.Equal(HealthState.Stopped,        status.Health);
    }

    [Fact]
    public async Task GetStatus_RunningWithStalePid_ReturnsCrashed()
    {
        var store = MakeStore(s =>
        {
            s.Readiness          = ReadinessState.Initialized;
            s.Health             = HealthState.Running;
            s.QemuPid            = 999999;    // non-existent PID
            s.QemuStartTimeTicks = DateTime.UtcNow.Ticks;
            s.LoopbackUrl        = "http://127.0.0.1:18080";
        });

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(HealthState.Crashed, status.Health);
        Assert.Null(status.LoopbackUrl); // cleared on crash
    }

    [Fact]
    public async Task GetStatus_OpenJournal_SurfacesRecoverOp()
    {
        var store = MakeStore(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.Health    = HealthState.Stopped;
            s.RecoveryJournal = new RecoveryJournal
            {
                TargetImagePath  = "/path/system.qcow2",
                DiskSwapped      = true,
                MariaDbUpgraded  = false,
                HealthPassed     = false,
            };
        });

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(OperationKind.Recover, status.ActiveOperation);
    }
    [Fact]
    public void RecordAcceptedBuild_WithDataInState_SetsReadinessToInitialized()
    {
        var store = MakeStore(s =>
        {
            s.Readiness = ReadinessState.NotBuilt;
            s.DataImagePath = Path.Combine(_dir, "data.img");
        });

        BuildOperation.RecordAcceptedBuild(store, Path.Combine(_dir, "system.qcow2"));

        var state = store.Read();
        Assert.Equal(ReadinessState.Initialized, state.Readiness);
        Assert.Equal(Path.Combine(_dir, "data.img"), state.DataImagePath);
    }

    [Fact]
    public void RecordAcceptedBuild_NoData_SetsReadinessToBuilt()
    {
        var store = MakeStore(s => s.Readiness = ReadinessState.NotBuilt);

        BuildOperation.RecordAcceptedBuild(store, Path.Combine(_dir, "system.qcow2"));

        var state = store.Read();
        Assert.Equal(ReadinessState.Built, state.Readiness);
        Assert.Null(state.DataImagePath);
    }

    // ── LifecycleLock busy detection ──────────────────────────────────────

    [Fact]
    public async Task SecondMutation_WhileFirstHeld_ReturnsBusy()
    {
        // Simulate: lifecycle lock is held by a background thread while
        // a second call (on the test thread) tries to acquire it.
        using var lk1 = new LifecycleLock();
        using var lk2 = new LifecycleLock();

        bool busyDetected = false;
        var lease = lk1.TryAcquire(TimeSpan.Zero);

        var t = new Thread(() =>
        {
            var lease2 = lk2.TryAcquire(TimeSpan.Zero);
            busyDetected = lease2 is null;
            lease2?.Dispose();
        });
        t.Start();
        t.Join();

        lease?.Dispose();
        Assert.True(busyDetected);
    }

    // ── State store: Initialized persists across re-reads ─────────────────

    [Fact]
    public void StateStore_WriteInitialized_RoundTrips()
    {
        var store = MakeStore();
        store.Mutate(s =>
        {
            s.Readiness     = ReadinessState.Initialized;
            s.DataImagePath = "/data/data.img";
        });

        var read = store.Read();

        Assert.Equal(ReadinessState.Initialized, read.Readiness);
        Assert.Equal("/data/data.img",           read.DataImagePath);
    }

    // ── Status: built-but-uninitialized never collapses to Initialized ────

    [Fact]
    public async Task GetStatus_Built_ReturnsBuiltNotInitialized()
    {
        var store = MakeStore(s =>
        {
            s.Readiness = ReadinessState.Built;
            s.Health    = HealthState.Stopped;
        });

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(ReadinessState.Built, status.Readiness);
        Assert.NotEqual(ReadinessState.Initialized, status.Readiness);
    }
}

[Collection("LifecycleLock")]
public sealed class ApplianceServicePreconditionTests : IDisposable
{
    // The service takes the lifecycle lease from the app-data folder. Against the real folder these
    // tests fail whenever Serpy or a setup run holds the lease, so they use a temporary root.
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyServicePrecondition-{Guid.NewGuid():N}");

    public ApplianceServicePreconditionTests() =>
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_dir, "appdata"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task BuildAsync_InitializedAppliance_RefusesReplacementOutsideRecover()
    {
        var service = CreateService(ReadinessState.Initialized);

        var result = await service.BuildAsync(NoProgress());

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Contains("recover", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitializeAsync_NotBuiltAppliance_RefusesBeforeCreatingDataDisk()
    {
        var service = CreateService(ReadinessState.NotBuilt);

        var result = await service.InitializeAsync(
            new InitializationParameters("site1.local", "secret"), NoProgress());

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Contains("build", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private ApplianceService CreateService(ReadinessState readiness)
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        store.Mutate(s => s.Readiness = readiness);
        return new ApplianceService(
            null!, null!, null!, null!, null!, null!, store, null!, null!);
    }

    private static IProgress<OperationUpdate> NoProgress() => new Progress<OperationUpdate>();
}

public sealed class ApplianceServiceBuildReadinessTests
{
    [Fact]
    public void CanBuildFrom_BuiltWithoutCommittedData_AllowsRetry()
    {
        var state = new ApplianceState { Readiness = ReadinessState.Built };

        Assert.True(ApplianceService.CanBuildFrom(state, committedDataExists: false));
    }

    [Fact]
    public void CanBuildFrom_NotBuiltWithStateDataPath_RejectsOverwritingPersistentData()
    {
        var state = new ApplianceState
        {
            Readiness = ReadinessState.NotBuilt,
            DataImagePath = "C:\\Serpy\\appliance\\data.img",
        };

        Assert.False(ApplianceService.CanBuildFrom(state, committedDataExists: false));
    }

    [Fact]
    public void CanBuildFrom_NotBuiltWithCommittedDataOnDisk_RejectsOverwritingPersistentData()
    {
        var state = new ApplianceState { Readiness = ReadinessState.NotBuilt };

        Assert.False(ApplianceService.CanBuildFrom(state, committedDataExists: true));
    }

    [Fact]
    public void CanBuildFrom_InitializedWithData_RejectsReplacementOutsideRecover()
    {
        var state = new ApplianceState
        {
            Readiness = ReadinessState.Initialized,
            DataImagePath = "C:\\Serpy\\appliance\\data.img",
        };

        Assert.False(ApplianceService.CanBuildFrom(state, committedDataExists: false));
    }
    [Fact]
    public void CanBuildPreservingDataFrom_DataPresent_ReturnsTrue()
    {
        var state = new ApplianceState { Readiness = ReadinessState.Initialized, DataImagePath = "C:\\Serpy\\appliance\\data.img" };
        Assert.True(ApplianceService.CanBuildPreservingDataFrom(state, committedDataExists: true));
    }

    [Fact]
    public void CanBuildPreservingDataFrom_NoData_ReturnsFalse()
    {
        var state = new ApplianceState { Readiness = ReadinessState.NotBuilt };
        Assert.False(ApplianceService.CanBuildPreservingDataFrom(state, committedDataExists: false));
    }
}

public sealed class ApplianceServiceVerifyPasswordTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyVerifyPwdTest-{Guid.NewGuid():N}");

    public ApplianceServiceVerifyPasswordTests() =>
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_dir, "appdata"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeGuestProbe : Serpy.Core.Guest.IGuestHealthProbe
    {
        public Task<string> ListAppsAsync(string site, CancellationToken ct) => Task.FromResult("erpnext 16.0.0\nfrappe 16.0.0");
        public Task<bool> IsSchedulerRunningAsync(string site, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeSiteHandler(System.Net.HttpStatusCode loginStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/api/method/login"))
            {
                var resp = new HttpResponseMessage(loginStatus);
                if (loginStatus == System.Net.HttpStatusCode.OK)
                    resp.Headers.Add("Set-Cookie", "sid=123; Path=/");
                return Task.FromResult(resp);
            }
            if (path.Contains("/api/resource/ToDo"))
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"name\":\"todo-1\"}}", System.Text.Encoding.UTF8, "application/json"),
                };
                return Task.FromResult(resp);
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task VerifyPassword_AcceptedPassword_SavesCredential_AndSetsHealthRunning()
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        store.Mutate(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.Health = HealthState.RunningUnhealthy;
            s.HealthFailureReason = "password needed";
            s.LoopbackUrl = "http://127.0.0.1:18080";
            s.QgaPort = 18001;
        });

        var creds = new HealthCredentials(Path.Combine(_dir, "health-cred"));
        var site = new FakeSiteHandler(System.Net.HttpStatusCode.OK);

        var service = new ApplianceService(
            runtimeResolver: null!,
            certStore: new TlsCertificateStore(Path.Combine(_dir, "certs")),
            imageTool: null!,
            imageDownloader: null!,
            seedWriter: null!,
            healthCredentials: creds,
            stateStore: store,
            manifest: new VersionManifest(),
            settings: new ApplianceSettings(),
            healthCheckerFactory: (siteName, url, c) =>
                new Serpy.Core.Health.HealthChecker(new FakeGuestProbe(), siteName, url, c, site));

        var result = await service.VerifyPasswordAsync("newSecretPassword");

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal("newSecretPassword", creds.Retrieve()?.AdminPassword);
        Assert.Equal(HealthState.Running, store.Read().Health);
        Assert.Null(store.Read().HealthFailureReason);
    }

    [Fact]
    public async Task VerifyPassword_RejectedPassword_ReturnsPasswordRejected_AndDoesNotSave()
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        store.Mutate(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.Health = HealthState.RunningUnhealthy;
            s.HealthFailureReason = "password needed";
            s.LoopbackUrl = "http://127.0.0.1:18080";
            s.QgaPort = 18001;
        });

        var creds = new HealthCredentials(Path.Combine(_dir, "health-cred"));
        var site = new FakeSiteHandler(System.Net.HttpStatusCode.Unauthorized);

        var service = new ApplianceService(
            runtimeResolver: null!,
            certStore: new TlsCertificateStore(Path.Combine(_dir, "certs")),
            imageTool: null!,
            imageDownloader: null!,
            seedWriter: null!,
            healthCredentials: creds,
            stateStore: store,
            manifest: new VersionManifest(),
            settings: new ApplianceSettings(),
            healthCheckerFactory: (siteName, url, c) =>
                new Serpy.Core.Health.HealthChecker(new FakeGuestProbe(), siteName, url, c, site));

        var result = await service.VerifyPasswordAsync("wrongPassword");

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.PasswordRejected, result.Code);
        Assert.Null(creds.Retrieve());
        Assert.Equal(HealthState.RunningUnhealthy, store.Read().Health);
        Assert.Equal("password needed", store.Read().HealthFailureReason);
    }

    [Fact]
    public async Task VerifyPassword_WhenApplianceStopped_ReturnsFailure()
    {
        Directory.CreateDirectory(_dir);
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        store.Mutate(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.Health = HealthState.Stopped;
        });

        var creds = new HealthCredentials(Path.Combine(_dir, "health-cred"));
        var service = new ApplianceService(
            runtimeResolver: null!,
            certStore: new TlsCertificateStore(Path.Combine(_dir, "certs")),
            imageTool: null!,
            imageDownloader: null!,
            seedWriter: null!,
            healthCredentials: creds,
            stateStore: store,
            manifest: new VersionManifest(),
            settings: new ApplianceSettings());

        var result = await service.VerifyPasswordAsync("any");

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Contains("not running", result.Message);
    }
}
