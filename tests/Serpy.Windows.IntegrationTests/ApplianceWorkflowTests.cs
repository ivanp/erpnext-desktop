using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Images;
using Serpy.Core.Health;
using Serpy.Core.Operations;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;

namespace Serpy.Windows.IntegrationTests;

/// <summary>
/// Serialized appliance lifecycle integration tests.
/// AE3 (persistence) and AE5 (durability) both operate the same QEMU process
/// and must not run concurrently. Placing them in one non-parallelized collection
/// keeps xUnit's scheduler out of the shared KnownPaths/StateStore/QEMU-PID space.
/// </summary>
[CollectionDefinition(nameof(ApplianceWorkflow), DisableParallelization = true)]
public sealed class ApplianceWorkflow { }

/// <summary>
/// Shared fixture: builds the composition root once per collection run and
/// validates the required environment before any test runs.
/// Fails setup immediately when SERPY_ADMIN_PASSWORD is absent/blank so every
/// test in the collection receives a clear error rather than a silent empty-credential run.
/// </summary>
public sealed class ApplianceWorkflowFixture : IAsyncLifetime
{
    private static string AdminPassword =>
        Environment.GetEnvironmentVariable("SERPY_ADMIN_PASSWORD") ?? string.Empty;

    private static string SiteName =>
        Environment.GetEnvironmentVariable("SERPY_SITE_NAME") ?? "site1.local";

    private static int ErpNextPort =>
        int.TryParse(Environment.GetEnvironmentVariable("SERPY_ERPNEXT_PORT"), out var p) ? p : 18080;

    public IApplianceService Service { get; private set; } = null!;
    public StateStore StateStore { get; private set; } = null!;
    public string ErpNextUrl => $"http://127.0.0.1:{ErpNextPort}";

    public async Task InitializeAsync()
    {
        // Early-out when the opt-in flag is absent — no validation, no construction.
        // Individual tests carry the same guard so they still skip in the default suite.
        if (Environment.GetEnvironmentVariable("SERPY_RUN_APPLIANCE") != "1")
            return;

        if (string.IsNullOrWhiteSpace(AdminPassword))
            throw new InvalidOperationException(
                "SERPY_RUN_APPLIANCE=1 requires SERPY_ADMIN_PASSWORD to be set and non-blank.");

        var manifest    = VersionManifestLoader.Load();
        var settings    = new ApplianceSettings { ErpNextPort = ErpNextPort };
        var resolver    = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = manifest.Qemu.Version,
            Windows     = new RuntimeManifest.WindowsBundle
            {
                InstallerUrl     = manifest.Qemu.Windows.InstallerUrl,
                Sha256           = manifest.Qemu.Windows.Sha256,
                ArchiveUrl       = manifest.Qemu.Windows.ArchiveUrl,
                ArchiveSha256    = manifest.Qemu.Windows.ArchiveSha256,
                SourceUrl        = manifest.Qemu.Windows.SourceUrl,
                LicenseNoticeUrl = manifest.Qemu.Windows.LicenseNoticeUrl,
            },
        });
        var certStore   = new TlsCertificateStore();
        var imageTool   = new QemuImageTool(resolver.QemuImgExe);
        StateStore      = new StateStore();
        var healthCreds = new HealthCredentials();
        healthCreds.Store(SiteName, AdminPassword);

        // Runtime must already be installed (SERPY_RUN_SMOKE installs it).
        // EnsureInstalledAsync is idempotent and no-ops when already present.
        await resolver.EnsureInstalledAsync(
            new Progress<string>(m => Console.WriteLine($"[resolver] {m}")));

        Service = new ApplianceService(
            resolver, certStore, imageTool,
            new BaseImageDownloader(manifest),
            new NoCloudSeedWriter(
                NoCloudSeedWriter.RenderUserData(string.Empty, string.Empty, string.Empty, string.Empty),
                string.Empty),
            healthCreds, StateStore, manifest, settings);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Shared REST helpers ───────────────────────────────────────────────────

    public static async Task<string> AuthenticateAsync(string baseUrl, string password)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var body = new StringContent(
            JsonSerializer.Serialize(new { usr = "Administrator", pwd = password }),
            Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("/api/method/login", body);
        resp.EnsureSuccessStatusCode();
        return string.Join("; ", resp.Headers.TryGetValues("Set-Cookie", out var cv) ? cv : []);
    }

    public static async Task<string> CreateProbeDocAsync(string baseUrl, string cookie, string subject)
    {
        using var http = MakeClient(baseUrl, cookie);
        var body = new StringContent(
            JsonSerializer.Serialize(new { subject, status = "Open" }),
            Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("/api/resource/ToDo", body);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        var doc  = JsonSerializer.Deserialize<JsonElement>(json);
        return doc.GetProperty("data").GetProperty("name").GetString()!;
    }

    public static async Task<bool> RecordExistsAsync(string baseUrl, string cookie, string name)
    {
        using var http = MakeClient(baseUrl, cookie);
        var resp = await http.GetAsync($"/api/resource/ToDo/{Uri.EscapeDataString(name)}");
        return resp.IsSuccessStatusCode;
    }

    public static async Task DeleteProbeDocAsync(string baseUrl, string cookie, string name)
    {
        using var http = MakeClient(baseUrl, cookie);
        await http.DeleteAsync($"/api/resource/ToDo/{Uri.EscapeDataString(name)}");
    }

    public static HttpClient MakeClient(string baseUrl, string cookie)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        http.DefaultRequestHeaders.Add("Cookie", cookie);
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        return http;
    }
}

// ── AE3: Persistence across a clean stop/restart ─────────────────────────────

/// <summary>
/// AE3 (R7): A record created before a clean stop/restart is still present afterwards.
///
/// Opt-in: SERPY_RUN_APPLIANCE=1 + SERPY_ADMIN_PASSWORD on a WHPX-enabled machine
/// with a provisioned appliance workspace (system.qcow2 + data.img).
/// </summary>
[Collection(nameof(ApplianceWorkflow))]
public sealed class PersistenceTests(ApplianceWorkflowFixture fx)
    : IClassFixture<ApplianceWorkflowFixture>
{
    private static bool ShouldRun =>
        Environment.GetEnvironmentVariable("SERPY_RUN_APPLIANCE") == "1";

    private static string AdminPassword =>
        Environment.GetEnvironmentVariable("SERPY_ADMIN_PASSWORD") ?? string.Empty;

    [Fact]
    public async Task PersistenceAcrossCleanRestart_RecordSurvives()
    {
        if (!ShouldRun)
        {
            Console.WriteLine(
                "SKIP PersistenceAcrossCleanRestart: set SERPY_RUN_APPLIANCE=1 " +
                "SERPY_ADMIN_PASSWORD=<pwd> on a provisioned Windows WHPX machine.");
            return;
        }

        var progress = NoProgress();

        // 1. Ensure appliance is running.
        var startResult = await fx.Service.StartAsync(progress, Ct());
        Assert.Equal(OperationOutcome.Success, startResult.Outcome);

        // 2. Authenticate and create a probe record.
        string cookie  = await ApplianceWorkflowFixture.AuthenticateAsync(fx.ErpNextUrl, AdminPassword);
        string docName = await ApplianceWorkflowFixture.CreateProbeDocAsync(fx.ErpNextUrl, cookie, "AE3-persistence-probe");
        Console.WriteLine($"[AE3] Created record: {docName}");

        // 3. Graceful stop — confirmed via QMP SHUTDOWN event.
        var stopResult = await fx.Service.StopAsync(progress, Ct());
        Assert.Equal(OperationOutcome.Success, stopResult.Outcome);
        Console.WriteLine("[AE3] Clean stop confirmed.");

        // 4. Restart.
        var restartResult = await fx.Service.StartAsync(progress, Ct());
        Assert.Equal(OperationOutcome.Success, restartResult.Outcome);
        Console.WriteLine("[AE3] Restart confirmed.");

        // 5. Verify record persists.
        cookie = await ApplianceWorkflowFixture.AuthenticateAsync(fx.ErpNextUrl, AdminPassword);
        bool exists = await ApplianceWorkflowFixture.RecordExistsAsync(fx.ErpNextUrl, cookie, docName);
        Assert.True(exists, $"[AE3] Record {docName} not found after clean restart.");
        Console.WriteLine($"[AE3] Record {docName} survived clean restart. ✅");

        // 6. Cleanup.
        await ApplianceWorkflowFixture.DeleteProbeDocAsync(fx.ErpNextUrl, cookie, docName);
    }

    private static CancellationToken Ct() =>
        new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token;

    private static IProgress<OperationUpdate> NoProgress() =>
        new Progress<OperationUpdate>(u => Console.WriteLine($"[{u.Stage}] {u.Message}"));
}

// ── AE5: Durability after unclean QEMU kill ───────────────────────────────────

/// <summary>
/// AE5 (R7): An acknowledged committed record survives an unclean QEMU kill + restart.
/// Result (pass or fail) is product evidence and must be recorded in docs/results.md.
///
/// Opt-in: SERPY_RUN_APPLIANCE=1 + SERPY_ADMIN_PASSWORD on a WHPX-enabled machine
/// with a provisioned appliance workspace (system.qcow2 + data.img).
/// </summary>
[Collection(nameof(ApplianceWorkflow))]
public sealed class DurabilityTests(ApplianceWorkflowFixture fx)
    : IClassFixture<ApplianceWorkflowFixture>
{
    private static bool ShouldRun =>
        Environment.GetEnvironmentVariable("SERPY_RUN_APPLIANCE") == "1";

    private static string AdminPassword =>
        Environment.GetEnvironmentVariable("SERPY_ADMIN_PASSWORD") ?? string.Empty;

    [Fact]
    public async Task DurabilityAfterUncleanKill_RecordObserved()
    {
        if (!ShouldRun)
        {
            Console.WriteLine(
                "SKIP DurabilityAfterUncleanKill: set SERPY_RUN_APPLIANCE=1 " +
                "SERPY_ADMIN_PASSWORD=<pwd> on a provisioned Windows WHPX machine.");
            return;
        }

        var progress = NoProgress();

        // 1. Start the appliance.
        var startResult = await fx.Service.StartAsync(progress, Ct());
        Assert.Equal(OperationOutcome.Success, startResult.Outcome);

        // 2. Create and acknowledge a probe record.
        string cookie  = await ApplianceWorkflowFixture.AuthenticateAsync(fx.ErpNextUrl, AdminPassword);
        string docName = await ApplianceWorkflowFixture.CreateProbeDocAsync(fx.ErpNextUrl, cookie, "AE5-durability-probe");
        Console.WriteLine($"[AE5] Created record: {docName}");

        // Allow InnoDB commit propagation (innodb_flush_log_at_trx_commit=1).
        await Task.Delay(TimeSpan.FromSeconds(3));

        // 3. Hard-kill QEMU using the persisted Windows PID identity.
        var state = fx.StateStore.Read();
        Assert.NotNull(state.QemuPid);
        int  pid        = state.QemuPid!.Value;
        long startTicks = state.QemuStartTimeTicks ?? 0;

        Assert.True(ProcessIdentity.IsAlive(pid, startTicks),
            $"QEMU process {pid} was not alive before the kill — start state may be stale.");

        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
        catch (Exception ex) { Console.WriteLine($"[AE5] Kill error: {ex.Message}"); }

        Console.WriteLine($"[AE5] Hard-killed QEMU PID {pid}.");

        // Reconcile state: StopOperation uses QMP, which is unavailable after a hard kill.
        // Write the stopped state directly so StartAsync's preflight passes.
        fx.StateStore.Mutate(s =>
        {
            s.Health     = HealthState.Stopped;
            s.QemuPid    = null;
            s.QmpPort    = null;
            s.QgaPort    = null;
            s.SerialPort = null;
            s.LoopbackUrl = null;
        });

        // Allow Windows to release file handles before the next start.
        await Task.Delay(TimeSpan.FromSeconds(5));

        // 4. Restart.
        var restartResult = await fx.Service.StartAsync(progress, Ct());
        Assert.Equal(OperationOutcome.Success, restartResult.Outcome);
        Console.WriteLine("[AE5] Restart after unclean kill confirmed.");

        // 5. Observe record presence — this is product evidence, not an assumed pass.
        cookie = await ApplianceWorkflowFixture.AuthenticateAsync(fx.ErpNextUrl, AdminPassword);
        bool exists = await ApplianceWorkflowFixture.RecordExistsAsync(fx.ErpNextUrl, cookie, docName);

        Console.WriteLine(exists
            ? $"[AE5] ✅ Record {docName} SURVIVED unclean kill — InnoDB WAL commit confirmed."
            : $"[AE5] ⚠️  Record {docName} NOT FOUND after unclean kill. " +
              "Document the observed MariaDB InnoDB recovery output in docs/results.md.");

        // A durability failure is product truth, not a reason to skip the assertion.
        Assert.True(exists,
            $"AE5 durability: record {docName} was not present after unclean kill/restart. " +
            "Populate docs/results.md with the observed recovery outcome.");

        // 6. Cleanup.
        await ApplianceWorkflowFixture.DeleteProbeDocAsync(fx.ErpNextUrl, cookie, docName);
    }

    private static CancellationToken Ct() =>
        new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token;

    private static IProgress<OperationUpdate> NoProgress() =>
        new Progress<OperationUpdate>(u => Console.WriteLine($"[{u.Stage}] {u.Message}"));
}
