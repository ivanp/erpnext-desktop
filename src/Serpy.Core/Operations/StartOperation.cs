using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Guest;
using Serpy.Core.Health;
using Serpy.Core.Images;
using Serpy.Core.Protocols.Qga;
using Serpy.Core.Protocols.Qmp;
using Serpy.Core.Qemu;

namespace Serpy.Core.Operations;

/// <summary>
/// Start the appliance (KTD8, R13, R15, R16).
///
/// Sequence:
///   1. Preflight: readiness, port not foreign-owned, WHPX probe, recovery journal check.
///   2. Write durable Starting record (generation + endpoints) BEFORE spawning.
///   3. Spawn QEMU; persist PID + start-time immediately.
///   4. Connect QMP with mTLS; confirm query-status.
///   5. Promote state to Running; expose loopback URL.
///   6. A live matching instance returns its URL rather than spawning a second VM.
/// </summary>
public sealed class StartOperation(
    ManagedRuntimeResolver runtimeResolver,
    TlsCertificateStore certStore,
    HealthCredentials healthCredentials,
    StateStore stateStore,
    ApplianceSettings settings)
{
    private static readonly TimeSpan QmpConnectTimeout = TimeSpan.FromSeconds(30);

    public async Task<OperationResult> ExecuteAsync(
        Guid operationId,
        IProgress<OperationUpdate> progress,
        CancellationToken ct)
    {
        var logPath = Path.Combine(KnownPaths.LogsDir, $"start-{operationId:N}.log");
        Directory.CreateDirectory(KnownPaths.LogsDir);

        void Report(string stage, string msg, int? pct = null) =>
            progress.Report(new OperationUpdate(
                operationId, OperationKind.Start, stage,
                UpdateSeverity.Info, msg, pct, null));

        var state = stateStore.Read();

        // Guard: already running matching instance.
        if (state.Health == HealthState.Running &&
            state.QemuPid.HasValue &&
            ProcessIdentity.IsAlive(state.QemuPid.Value, state.QemuStartTimeTicks ?? 0))
        {
            return new OperationResult(operationId, OperationKind.Start,
                OperationOutcome.Success,
                $"Appliance already running at {state.LoopbackUrl}", logPath);
        }

        // Guard: recovery journal blocks start.
        if (state.RecoveryJournal is { HealthPassed: false })
            return Fail(operationId,
                "A recovery operation was interrupted before the health gate. " +
                "Run Recover to complete migration before starting.", logPath);

        // Guard: readiness.
        if (state.Readiness != ReadinessState.Initialized)
            return Fail(operationId,
                $"Cannot start: readiness is {state.Readiness}. Build and Initialize first.",
                logPath);

        var systemImagePath = state.SystemImagePath ?? Path.Combine(KnownPaths.ApplianceDir, "system.qcow2");
        if (!SystemImageManifest.IsAccepted(systemImagePath))
            return Fail(operationId,
                "System image is missing a valid Serpy acceptance manifest. Build or recover before starting.",
                logPath);

        // Preflight: WHPX probe.
        Report("preflight", "Probing WHPX accelerator…", 5);
        var accel = AcceleratorPolicy.Resolve();
        if (accel.Accelerator == AcceleratorKind.Whpx)
        {
            var probe = await WhpxProbe.RunAsync(
                runtimeResolver.QemuSystemExe, runtimeResolver.ShareDir, ct);
            if (!probe.Success)
                return Fail(operationId, probe.Message, logPath);
        }

        // Allocate ephemeral ports.
        int qmpPort    = EphemeralPort.Allocate();
        int qgaPort    = EphemeralPort.Allocate();
        int serialPort = EphemeralPort.Allocate();
        long generation = state.Generation + 1;

        // Write durable Starting record BEFORE spawning (KTD8).
        Report("starting", "Writing start record…", 10);
        stateStore.Mutate(s =>
        {
            s.Health       = HealthState.Starting;
            s.Generation   = generation;
            s.QmpPort      = qmpPort;
            s.QgaPort      = qgaPort;
            s.SerialPort   = serialPort;
            s.ActiveOperation = OperationKind.Start;
            s.LogPath      = logPath;
        });

        if (!certStore.IsInitialized()) certStore.GenerateCertificates();

        var args = new QemuArguments()
            .VmName($"serpy-gen-{generation}")
            .Machine("q35").Accelerator(accel).Cpu()
            .Smp(settings.CpuCores).Memory(settings.MemoryMb).Headless()
            .FirmwareDir(runtimeResolver.ShareDir)
            .SystemDisk(state.SystemImagePath ?? Path.Combine(KnownPaths.ApplianceDir, "system.qcow2"))
            .DataDisk(state.DataImagePath ?? Path.Combine(KnownPaths.ApplianceDir, "data.img"), accel)
            .UserNetWithPortForward(settings.ErpNextPort)
            .TlsCredsX509("tls-qmp",    certStore.QemuCertDir)
            .TlsCredsX509("tls-qga",    certStore.QemuCertDir)
            .TlsChardev("qmp0",    qmpPort,    "tls-qmp")
            .TlsChardev("qga0",    qgaPort,    "tls-qga")
            .QmpOnChardev("qmp0")
            .VirtioSerialDevice().QgaVirtioPort("qga0");

        // Spawn.
        Report("spawn", "Starting QEMU…", 15);
        var proc = QemuProcess.Start(runtimeResolver.QemuSystemExe, args.Args, logPath);

        // Persist PID immediately after spawn.
        stateStore.Mutate(s =>
        {
            s.QemuPid           = proc.Pid;
            s.QemuStartTimeTicks = ProcessIdentity.StartTimeTicks(
                System.Diagnostics.Process.GetProcessById(proc.Pid));
        });

        // Connect QMP and confirm.
        Report("qmp", "Waiting for QMP…", 25);
        var clientCert = certStore.LoadClientCert();
        var caCert     = certStore.LoadCaCert();

        try
        {
            using var cts = System.Threading.CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            cts.CancelAfter(QmpConnectTimeout);

            await using var qmp = await QmpClient.ConnectAsync(
                "127.0.0.1", qmpPort, clientCert, caCert, cts.Token);

            var statusResult = await qmp.ExecuteAsync("query-status", ct: cts.Token);
            if (statusResult is null)
                return await AbortStartAsync(proc, operationId,
                    "QMP query-status returned no response.", logPath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await CancelStartAsync(proc, operationId, logPath);
        }
        catch (Exception ex)
        {
            return await AbortStartAsync(proc, operationId,
                $"QMP connection failed: {ex.Message}", logPath);
        }
        var loopbackUrl = $"http://127.0.0.1:{settings.ErpNextPort}";
        Report("health", "Waiting for ERPNext functional health…", 60);
        try
        {
            var credentials = healthCredentials.Retrieve();
            var siteName = credentials?.SiteName ?? "site1.local";
            var health = await WaitForHealthyAsync(async token =>
            {
                try
                {
                    await using var qga = await QgaClient.ConnectAsync(
                        "127.0.0.1", qgaPort, clientCert, caCert, token);
                    return await new HealthChecker(
                        new GuestOperations(qga), siteName, loopbackUrl, healthCredentials).RunAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return HealthResult.Fail($"Guest agent is not ready: {ex.Message}", "guest-agent");
                }
            }, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), ct);
            if (!health.Healthy)
                return await AbortStartAsync(proc, operationId,
                    $"Functional health check failed [{health.FailedCheck}]: {health.Reason}", logPath);
        }
        catch (OperationCanceledException)
        {
            return await CancelStartAsync(proc, operationId, logPath);
        }
        catch (Exception ex)
        {
            return await AbortStartAsync(proc, operationId,
                $"Functional health check failed: {ex.Message}", logPath);
        }
        stateStore.Mutate(s =>
        {
            s.Health          = HealthState.Running;
            s.LoopbackUrl     = loopbackUrl;
            s.ActiveOperation = null;
            s.SystemImagePath ??= Path.Combine(KnownPaths.ApplianceDir, "system.qcow2");
            s.DataImagePath   ??= Path.Combine(KnownPaths.ApplianceDir, "data.img");
        });

        Report("running", $"Appliance running at {loopbackUrl}", 100);
        return new OperationResult(operationId, OperationKind.Start,
            OperationOutcome.Success, $"Started at {loopbackUrl}", logPath);
    }

    /// <summary>Polls the full R11 health contract until healthy or deadline.</summary>
    public static async Task<HealthResult> WaitForHealthyAsync(
        Func<CancellationToken, Task<HealthResult>> check,
        TimeSpan timeout,
        TimeSpan retryDelay,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = HealthResult.Fail("Timed out before functional health completed", "health");
        do
        {
            ct.ThrowIfCancellationRequested();
            last = await check(ct);
            if (last.Healthy) return last;
            if (retryDelay > TimeSpan.Zero && DateTime.UtcNow < deadline)
                await Task.Delay(retryDelay, ct);
        }
        while (DateTime.UtcNow < deadline);

        return last;
    }

    /// <summary>Stops a spawned VM and clears the durable in-progress record.</summary>
    public static void RecordCancelledStart(StateStore stateStore) =>
        stateStore.Mutate(s =>
        {
            s.Health = HealthState.Stopped;
            s.QemuPid = null;
            s.QemuStartTimeTicks = null;
            s.QmpPort = null;
            s.QgaPort = null;
            s.SerialPort = null;
            s.LoopbackUrl = null;
            s.ActiveOperation = null;
        });

    private async Task<OperationResult> CancelStartAsync(
        QemuProcess proc, Guid id, string log)
    {
        try { proc.Kill(); await proc.WaitForExitAsync(TimeSpan.FromSeconds(10)); }
        catch { /* best-effort cleanup after user cancellation */ }
        RecordCancelledStart(stateStore);
        return new OperationResult(id, OperationKind.Start, OperationOutcome.Cancelled,
            "Start cancelled.", log);
    }

    private async Task<OperationResult> AbortStartAsync(
        QemuProcess proc, Guid id, string msg, string log)
    {
        try { proc.Kill(); await proc.WaitForExitAsync(TimeSpan.FromSeconds(10)); }
        catch { /* best-effort */ }
        stateStore.Mutate(s =>
        {
            s.Health = HealthState.Crashed;
            s.ActiveOperation = null;
        });
        return Fail(id, msg, log);
    }

    private static OperationResult Fail(Guid id, string msg, string log) =>
        new(id, OperationKind.Start, OperationOutcome.Failure, msg, log);
}
