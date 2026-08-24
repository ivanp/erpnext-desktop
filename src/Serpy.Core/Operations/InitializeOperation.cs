using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Guest;
using Serpy.Core.Health;
using Serpy.Core.Images;
using Serpy.Core.Protocols;
using Serpy.Core.Protocols.Qga;
using Serpy.Core.Protocols.Qmp;
using Serpy.Core.Qemu;

namespace Serpy.Core.Operations;

/// <summary>
/// F2: Initialize the persistent data disk and first Frappe site (R14, KD4).
///
/// Sequence:
///   1. Reject if a committed data.img or marker already exists.
///   2. Create sparse RAW data.img under a .pending- name.
///   3. Boot system.qcow2 with the pending disk attached.
///   4. Await QGA; run init-data.sh via QGA (admin password via stdin, never args).
///   5. R11 functional health check.
///   6. Graceful powerdown.
///   7. Atomic rename → data.img; write committed marker; update state.
///
/// A pending disk that was never committed is NOT startable (KTD8).
/// </summary>
public sealed class InitializeOperation(
    QemuImageTool imageTool,
    ManagedRuntimeResolver runtimeResolver,
    TlsCertificateStore certStore,
    HealthCredentials healthCredentials,
    StateStore stateStore,
    ApplianceSettings settings)
{
    private const int DataDiskGb = 20;

    private string SystemImagePath => Path.Combine(KnownPaths.ApplianceDir, "system.qcow2");
    private string FinalDataPath   => Path.Combine(KnownPaths.ApplianceDir, "data.img");
    private string PendingDataPath => Path.Combine(KnownPaths.ApplianceDir, ".pending-data.img");
    private string DataMarkerPath  => Path.Combine(KnownPaths.ApplianceDir, ".data-committed");

    public async Task<OperationResult> ExecuteAsync(
        Guid operationId,
        string siteName,
        string adminPassword,
        IProgress<OperationUpdate> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
        Directory.CreateDirectory(KnownPaths.LogsDir);
        var logPath = Path.Combine(KnownPaths.LogsDir, $"init-{operationId:N}.log");

        void Report(string stage, string msg, int? pct = null) =>
            progress.Report(new OperationUpdate(
                operationId, OperationKind.Initialize, stage,
                UpdateSeverity.Info, msg, pct, null));

        // Guard: refuse if already committed.
        if (File.Exists(DataMarkerPath) || File.Exists(FinalDataPath))
            return Fail(operationId,
                "A committed data disk already exists. " +
                "Recover replaces the system image only; existing data is never silently overwritten.",
                logPath);

        if (!SystemImageManifest.IsAccepted(SystemImagePath))
            return Fail(operationId,
                "System image is missing a valid Serpy acceptance manifest. Build a verified system image before initialization.",
                logPath);

        try
        {
            // 1. Create pending data disk.
            Report("disk", $"Creating {DataDiskGb} GiB sparse data disk…", 5);
            if (File.Exists(PendingDataPath)) File.Delete(PendingDataPath);
            await imageTool.CreateSparseRawAsync(PendingDataPath, DataDiskGb, ct);

            // 2. TLS + port allocation.
            if (!certStore.IsInitialized()) certStore.GenerateCertificates();
            var clientCert = certStore.LoadClientCert();
            var caCert     = certStore.LoadCaCert();
            var accel      = AcceleratorPolicy.Resolve();

            int serialPort = EphemeralPort.Allocate();
            int qmpPort    = EphemeralPort.Allocate();
            int qgaPort    = EphemeralPort.Allocate();

            var args = new QemuArguments()
                .VmName($"serpy-init-{operationId:N}")
                .Machine().Accelerator(accel).Cpu(accel)
                .Smp(settings.CpuCores).Memory(settings.MemoryMb).Headless()
                .FirmwareDir(runtimeResolver.ShareDir)
                .SystemDisk(SystemImagePath)
                .DataDisk(PendingDataPath, accel)
                .UserNetWithPortForward(settings.ErpNextPort)
                .TlsCredsX509("tls-serial", certStore.QemuCertDir)
                .TlsCredsX509("tls-qmp",    certStore.QemuCertDir)
                .TlsCredsX509("tls-qga",    certStore.QemuCertDir)
                .TlsChardev("serial0", serialPort, "tls-serial")
                .SerialOnChardev("serial0")
                .TlsChardev("qmp0",    qmpPort,    "tls-qmp")
                .TlsChardev("qga0",    qgaPort,    "tls-qga")
                .QmpOnChardev("qmp0")
                .VirtioSerialDevice().QgaVirtioPort("qga0");

            // 3. Boot.
            Report("boot", "Booting VM for data initialization…", 15);
            await using var proc = QemuProcess.Start(
                runtimeResolver.QemuSystemExe, args.Args, logPath);

            // 4. QGA connection (via serial sentinel first).
            Report("qga", "Waiting for guest agent…", 20);
            await using var serial = await SerialClient.ConnectAsync(
                "127.0.0.1", serialPort, clientCert, caCert, ct);
            await using var qga = await QgaClient.ConnectAsync(
                "127.0.0.1", qgaPort, clientCert, caCert, ct);
            var guestOps = new GuestOperations(qga);

            // 5. init-data.sh via QGA (password via stdin, never in args).
            Report("init-data", $"Initializing site '{siteName}'… (20+ min)", 30);
            var result = await guestOps.RunInitDataAsync(
                siteName, adminPassword,
                timeout: TimeSpan.FromHours(1), ct: ct);

            if (!result.Succeeded)
                return await AbortAndRetainAsync(proc, operationId,
                    $"init-data.sh failed (exit {result.ExitCode}): {result.Stderr}",
                    logPath, ct);

            // 6. Health gate (R11).
            Report("health", "Running functional health check…", 85);
            healthCredentials.Store(siteName, adminPassword);

            var health = await new HealthChecker(
                guestOps, siteName,
                $"http://127.0.0.1:{settings.ErpNextPort}",
                healthCredentials).RunAsync(ct);

            if (!health.Healthy)
                return await AbortAndRetainAsync(proc, operationId,
                    $"Health check failed [{health.FailedCheck}]: {health.Reason}",
                    logPath, ct);

            // 7. Host-side authenticated graceful shutdown; cloud-init leaves the VM up.
            Report("powerdown", "Sending graceful powerdown via QMP…", 95);
            await using var qmp = await QmpClient.ConnectAsync(
                "127.0.0.1", qmpPort, clientCert, caCert, ct);
            await qmp.SendPowerdownAsync(ct);
            if (!await qmp.WaitForShutdownEventAsync(TimeSpan.FromMinutes(3), ct))
                return await AbortAndRetainAsync(proc, operationId,
                    "QMP did not confirm guest shutdown; pending data disk retained.", logPath, ct);
            if (!await proc.WaitForExitAsync(TimeSpan.FromMinutes(2), ct))
                return await AbortAndRetainAsync(proc, operationId,
                    "QEMU did not exit after graceful shutdown; pending data disk retained.", logPath, ct);

            // 8. Atomic commit.
            File.Move(PendingDataPath, FinalDataPath);
            File.WriteAllText(DataMarkerPath, $"committed:{DateTimeOffset.UtcNow:O}");
            stateStore.Mutate(s =>
            {
                s.Readiness     = ReadinessState.Initialized;
                s.DataImagePath = FinalDataPath;
            });

            Report("done", $"Site '{siteName}' initialized. data.img committed.", 100);
            return new OperationResult(operationId, OperationKind.Initialize,
                OperationOutcome.Success, $"Initialized '{siteName}'.", logPath);
        }
        catch (OperationCanceledException)
        {
            return new OperationResult(operationId, OperationKind.Initialize,
                OperationOutcome.Cancelled,
                "Initialization cancelled. Pending disk retained.", logPath);
        }
        catch (Exception ex)
        {
            return Fail(operationId, $"Initialization failed: {ex.Message}", logPath);
        }
    }

    private static async Task<OperationResult> AbortAndRetainAsync(
        QemuProcess proc, Guid id, string msg, string log, CancellationToken ct)
    {
        try { proc.Kill(); await proc.WaitForExitAsync(TimeSpan.FromSeconds(10), ct); }
        catch { /* best-effort */ }
        return Fail(id, msg, log);
    }

    private static OperationResult Fail(Guid id, string msg, string log) =>
        new(id, OperationKind.Initialize, OperationOutcome.Failure, msg, log);
}
