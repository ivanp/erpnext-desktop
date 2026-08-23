using System.Text.Json;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Images;
using Serpy.Core.Protocols;
using Serpy.Core.Protocols.Qga;
using Serpy.Core.Protocols.Qmp;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;

namespace Serpy.Core.Operations;

/// <summary>
/// F1: Build the site-less system.qcow2.
///
/// Sequence (R8, R9, R10):
///   1. Download + verify Debian base image.
///   2. Write NoCloud seed ISO in-process (KTD10).
///   3. Create staging qcow2 copy via managed qemu-img.
///   4. Boot headlessly with cloud-init.
///   5. Wait for SERPY_PROVISION_DONE sentinel on authenticated serial channel (KTD9).
///   6. Query installed versions via QGA; run R9 gate.
///   7. Graceful powerdown; wait for exit.
///   8. Write acceptance manifest + atomic rename to system.qcow2.
///
/// Interruption safety: staging image and acceptance manifest are published only after all
/// gates pass. Lifecycle preflight rejects any system.qcow2 missing the manifest.
/// </summary>
public sealed class BuildOperation(
    BaseImageDownloader imageDownloader,
    PackageClosurePreflight packageClosurePreflight,
    NoCloudSeedWriter seedWriter,
    QemuImageTool imageTool,
    ManagedRuntimeResolver runtimeResolver,
    TlsCertificateStore certStore,
    StateStore stateStore,
    VersionManifest manifest,
    ApplianceSettings settings)
{
    private const string ProvisionSentinel = "SERPY_PROVISION_DONE";

    private string StagingPath  => Path.Combine(KnownPaths.ApplianceDir, ".staging-system.qcow2");
    private string FinalPath    => Path.Combine(KnownPaths.ApplianceDir, "system.qcow2");
    private string ManifestPath => Path.Combine(KnownPaths.ApplianceDir, SystemImageManifest.FileName);
    private string SeedIsoPath  => Path.Combine(KnownPaths.ApplianceDir, ".cloud-init-seed.iso");

    public async Task<OperationResult> ExecuteAsync(
        Guid operationId,
        IProgress<OperationUpdate> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
        Directory.CreateDirectory(KnownPaths.LogsDir);
        var logPath = Path.Combine(KnownPaths.LogsDir, $"build-{operationId:N}.log");

        void Report(string stage, string msg, int? pct = null, UpdateSeverity sev = UpdateSeverity.Info) =>
            progress.Report(new OperationUpdate(operationId, OperationKind.Build, stage, sev, msg, pct, null));

        try
        {
            // 1. Resolve locked guest inputs before downloading or provisioning.
            Report("preflight", "Verifying locked guest package closure…", 1);
            await packageClosurePreflight.ValidateAsync(ct);

            // 2. Base image
            Report("download", "Downloading Debian base image…", 5);
            await imageDownloader.EnsureAsync(
                new Progress<string>(m => Report("download", m)), ct);

            // 3. Seed ISO
            Report("seed", "Writing cloud-init seed ISO…", 15);
            seedWriter.Write(SeedIsoPath);

            // 4. Staging image
            Report("image", "Creating staging system image…", 20);
            if (File.Exists(StagingPath)) File.Delete(StagingPath);
            await imageTool.CreateCopyAsync(imageDownloader.CachedImagePath, StagingPath, ct);
            await imageTool.ResizeAsync(StagingPath, 32, ct);

            // 5. TLS material
            if (!certStore.IsInitialized()) certStore.GenerateCertificates();
            var clientCert = certStore.LoadClientCert();
            var caCert     = certStore.LoadCaCert();
            var accel      = AcceleratorPolicy.Resolve();

            int serialPort = EphemeralPort.Allocate();
            int qmpPort    = EphemeralPort.Allocate();
            int qgaPort    = EphemeralPort.Allocate();

            var args = new QemuArguments()
                .VmName("serpy-build")
                .Machine().Accelerator(accel).Cpu()
                .Smp(settings.CpuCores).Memory(settings.MemoryMb).Headless()
                .FirmwareDir(runtimeResolver.ShareDir)
                .SystemDisk(StagingPath).Cdrom(SeedIsoPath)
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

            // 6. Boot
            Report("provision", "Booting provisioning VM…", 25);
            await using var proc = QemuProcess.Start(runtimeResolver.QemuSystemExe, args.Args, logPath);

            // 7. Serial sentinel
            Report("provision", "Waiting for cloud-init (30–60 min)…", 30);
            await using var serial = await SerialClient.ConnectAsync(
                "127.0.0.1", serialPort, clientCert, caCert, ct);

            var sentinel = await serial.WaitForSentinelAsync(
                ProvisionSentinel,
                TimeSpan.FromHours(2),
                new Progress<string>(line => progress.Report(
                    new OperationUpdate(operationId, OperationKind.Build, "provision",
                        UpdateSeverity.Info, line, null, line + "\n"))),
                ct);

            if (!sentinel.Found)
            {
                await KillAndClean(proc, ct);
                return Fail(operationId, "Cloud-init did not complete within 2 hours.", logPath);
            }

            // 8. QGA version gate (R9)
            Report("version-gate", "Querying installed versions…", 85);
            await using var qga = await QgaClient.ConnectAsync(
                "127.0.0.1", qgaPort, clientCert, caCert, ct);

            var versions = await QueryVersionsAsync(qga, ct);
            Report("version-gate",
                $"python={versions.Python} node={versions.Node} " +
                $"mariadb={versions.MariaDb} redis={versions.Redis} " +
                $"frappe={versions.Frappe} erpnext={versions.ErpNext}", 88);

            try { VersionGate.Validate(versions, manifest); }
            catch (VersionGateException ex)
            {
                await KillAndClean(proc, ct);
                RemoveStaging();
                return Fail(operationId,
                    $"Version gate: component={ex.Component} installed={ex.Installed} " +
                    $"locked={ex.Locked} floor={ex.Floor}", logPath);
            }

            // 9. Cloud-init keeps the VM running; the host owns its authenticated shutdown.
            await using var qmp = await QmpClient.ConnectAsync(
                "127.0.0.1", qmpPort, clientCert, caCert, ct);
            await qmp.SendPowerdownAsync(ct);
            if (!await qmp.WaitForShutdownEventAsync(TimeSpan.FromMinutes(3), ct))
                proc.Kill();
            if (!await proc.WaitForExitAsync(TimeSpan.FromMinutes(2), ct))
                proc.Kill();

            // 10. Atomic image rename, then attest to the final exact bytes.
            if (File.Exists(FinalPath)) File.Delete(FinalPath);
            File.Move(StagingPath, FinalPath);

            var accepted = new SystemImageManifest
            {
                BuildTimestamp = DateTimeOffset.UtcNow,
                ImageSha256 = ComputeSha256(FinalPath),
                Versions = versions,
            };
            File.WriteAllText(ManifestPath,
                JsonSerializer.Serialize(accepted,
                    ApplianceStateJsonContext.Default.SystemImageManifest));

            RecordAcceptedBuild(stateStore, FinalPath);

            Report("done",
                $"system.qcow2 built. Python={versions.Python} " +
                $"Node={versions.Node} MariaDB={versions.MariaDb} Redis={versions.Redis}", 100);
            return new OperationResult(operationId, OperationKind.Build,
                OperationOutcome.Success,
                "Build complete — system.qcow2 accepted.", logPath);
        }
        catch (OperationCanceledException)
        {
            RemoveStaging();
            return new OperationResult(operationId, OperationKind.Build,
                OperationOutcome.Cancelled, "Build cancelled.", logPath);
        }
        catch (Exception ex)
        {
            RemoveStaging();
            return Fail(operationId, $"Build failed: {ex.Message}", logPath);
        }
    }

    /// <summary>
    /// Publishes the durable lifecycle transition only after the final image and
    /// its acceptance manifest have both been written (F1 → F2).
    /// </summary>
    public static void RecordAcceptedBuild(StateStore stateStore, string systemImagePath) =>
        stateStore.Mutate(s =>
        {
            s.Readiness = ReadinessState.Built;
            s.Health = HealthState.Stopped;
            s.SystemImagePath = systemImagePath;
            s.DataImagePath = null;
            s.QemuPid = null;
            s.QemuStartTimeTicks = null;
            s.QmpPort = null;
            s.QgaPort = null;
            s.SerialPort = null;
            s.LoopbackUrl = null;
            s.ActiveOperation = null;
            s.LogPath = null;
            s.RecoveryJournal = null;
        });

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<GuestVersionReport> QueryVersionsAsync(
        QgaClient qga, CancellationToken ct)
    {
        async Task<string> Run(string cmd, string[] args)
        {
            var result = await qga.ExecAsync(cmd, args, ct: ct);
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    $"Version query failed: {cmd} exit={result.ExitCode}: {result.Stderr}");
            return result.Stdout.Trim();
        }

        var benchOutput = await Run("su", ["-", "frappe", "-c",
            "cd /home/frappe/frappe-bench && bench version"]);
        var apps = BenchVersionParser.ParseRequired(benchOutput);

        return new GuestVersionReport
        {
            Python  = await Run("python3.14", ["--version"]),
            Node    = await Run("node", ["--version"]),
            MariaDb = await Run("mariadb", ["--version"]),
            Redis   = await Run("redis-server", ["--version"]),
            Frappe  = apps.Frappe,
            ErpNext = apps.ErpNext,
        };
    }

    private static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static async Task KillAndClean(QemuProcess proc, CancellationToken ct)
    {
        proc.Kill();
        await proc.WaitForExitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    private void RemoveStaging()
    {
        if (File.Exists(StagingPath)) File.Delete(StagingPath);
    }

    private static OperationResult Fail(Guid id, string msg, string log) =>
        new(id, OperationKind.Build, OperationOutcome.Failure, msg, log);
}
