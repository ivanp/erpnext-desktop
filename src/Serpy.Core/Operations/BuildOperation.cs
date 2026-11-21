using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Downloads;
using Serpy.Core.Guest;
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
///   3. Pack offline cache into transport tar (KTD29) and create scratch raw disk.
///   4. Create staging qcow2 copy via managed qemu-img.
///   5. Boot headlessly with restricted networking (KTD30) and cloud-init.
///   6. Wait for SERPY_PROVISION_DONE sentinel on authenticated serial channel (KTD9).
///   7. Query installed versions via QGA; run R9 gate.
///   8. Graceful powerdown; wait for exit.
///   9. Write acceptance manifest + atomic rename to system.qcow2.
///
/// Interruption safety: staging image and acceptance manifest are published only after all
/// gates pass. Lifecycle preflight rejects any system.qcow2 missing the manifest.
/// </summary>
public sealed class BuildOperation(
    BaseImageDownloader? imageDownloader,
    NoCloudSeedWriter? seedWriter,
    QemuImageTool? imageTool,
    ManagedRuntimeResolver? runtimeResolver,
    TlsCertificateStore certStore,
    StateStore stateStore,
    VersionManifest manifest,
    ApplianceSettings settings,
    BuildInputs? buildInputs = null,
    DownloadCache? downloadCache = null,
    Func<long>? freeSpaceBytes = null,
    Func<string, IReadOnlyList<string>, string?, OperationLog?, bool, QemuProcess>? startProcess = null,
    Func<string, int, CancellationToken, Task>? createSparseRaw = null,
    Func<int, X509Certificate2, X509Certificate2, CancellationToken, Task<IAsyncEnumerable<string>>>? connectSerialLines = null,
    Func<int, X509Certificate2, X509Certificate2, CancellationToken, Task<GuestVersionReport>>? queryVersions = null,
    Func<int, X509Certificate2, X509Certificate2, CancellationToken, Task>? sendPowerdown = null)
{
    /// <summary>The guest must print its started marker within this time of the boot (KTD2).</summary>
    public static readonly TimeSpan ProvisionStartWindow = TimeSpan.FromMinutes(5);

    /// <summary>No console line for this long raises the quiet signal; the build keeps waiting.</summary>
    public static readonly TimeSpan ProvisionQuietTime = TimeSpan.FromMinutes(10);

    /// <summary>Longest the whole provisioning may take.</summary>
    public static readonly TimeSpan ProvisionTimeout = TimeSpan.FromHours(2);

    public const int ScratchDiskGb = 12;

    public string StagingPath      => Path.Combine(KnownPaths.ApplianceDir, ".staging-system.qcow2");
    public string FinalPath        => Path.Combine(KnownPaths.ApplianceDir, "system.qcow2");
    public string ManifestPath     => Path.Combine(KnownPaths.ApplianceDir, SystemImageManifest.FileName);
    public string SeedIsoPath      => Path.Combine(KnownPaths.ApplianceDir, ".cloud-init-seed.iso");
    public string TransportTarPath => Path.Combine(KnownPaths.ApplianceDir, ".cache-transport.tar");
    public string ScratchDiskPath  => Path.Combine(KnownPaths.ApplianceDir, ".scratch.raw");

    public async Task<OperationResult> ExecuteAsync(
        Guid operationId,
        IProgress<OperationUpdate> progress,
        CancellationToken ct) =>
        await ExecuteAsync(operationId, progress, preserveData: false, ct);

    public async Task<OperationResult> ExecuteAsync(
        Guid operationId,
        IProgress<OperationUpdate> progress,
        bool preserveData,
        CancellationToken ct)
    {
        var opKind = preserveData ? OperationKind.BuildPreservingData : OperationKind.Build;
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
        Directory.CreateDirectory(KnownPaths.LogsDir);
        var logPath = Path.Combine(KnownPaths.LogsDir, $"build-{operationId:N}.log");

        using var log = new OperationLog(logPath);
        stateStore.Mutate(s => s.LogPath = logPath);

        void Report(string stage, string msg, int? pct = null, UpdateSeverity sev = UpdateSeverity.Info)
        {
            log.Stage(stage, msg);
            progress.Report(new OperationUpdate(operationId, opKind, stage, sev, msg, pct, null));
        }

        try
        {
            // 1. Clean up leftovers from any earlier crashed or interrupted run (KTD10)
            RemoveStaging();
            DeleteIfPresent(SeedIsoPath);
            CacheTransport.Delete(TransportTarPath);
            DeleteIfPresent(ScratchDiskPath);

            var inputs = buildInputs ?? TryLoadBuildInputs();
            var cache = downloadCache ?? new DownloadCache();

            // 2. Free disk space guard BEFORE any packing or file creation
            var guestFiles = (inputs.Parts ?? Enumerable.Empty<InputPart>())
                .SelectMany(p => p.Files ?? Enumerable.Empty<InputFile>())
                .Where(f => f.InGuest)
                .ToList();
            long guestBytes = guestFiles.Sum(f => f.Size);
            long scratchBytes = (long)ScratchDiskGb * 1024 * 1024 * 1024;
            long requiredSpace = (guestBytes > 0 ? guestBytes : 1024 * 1024) + scratchBytes;

            long freeSpace = freeSpaceBytes?.Invoke() ?? GetAvailableFreeSpace(KnownPaths.ApplianceDir);
            if (freeSpace < requiredSpace)
            {
                log.Info($"Insufficient disk space for transport and scratch disks. Required: {requiredSpace} bytes, available: {freeSpace} bytes.");
                return Fail(operationId, opKind,
                    $"Insufficient free disk space on {KnownPaths.ApplianceDir}. Required: {requiredSpace / (1024 * 1024)} MB, available: {freeSpace / (1024 * 1024)} MB.",
                    logPath,
                    FailureCode.DiskFull);
            }

            // 3. Base image
            if (imageDownloader is not null)
            {
                Report("download", "Downloading Debian base image…", 5);
                await imageDownloader.EnsureAsync(
                    new Progress<string>(m => Report("download", m)), ct);
            }

            // 4. Seed ISO
            if (seedWriter is not null)
            {
                Report("seed", "Writing cloud-init seed ISO…", 15);
                seedWriter.Write(SeedIsoPath);
            }

            // 5. Pack offline transport cache (re-verifies checksums during copy; fails fast on mismatch)
            Report("transport", "Packing offline transport cache…", 18);
            CacheTransport.WriteTar(TransportTarPath, inputs, cache, ct);

            // 6. Temporary scratch disk for in-guest cache unpack and git materialization
            Report("scratch", "Creating temporary scratch disk…", 19);
            if (createSparseRaw is not null)
            {
                await createSparseRaw(ScratchDiskPath, ScratchDiskGb, ct);
            }
            else if (imageTool is not null)
            {
                await imageTool.CreateSparseRawAsync(ScratchDiskPath, ScratchDiskGb, ct);
            }
            else
            {
                File.WriteAllBytes(ScratchDiskPath, new byte[512]);
            }

            // 7. Staging image
            Report("image", "Creating staging system image…", 20);
            if (File.Exists(StagingPath)) File.Delete(StagingPath);
            var baseImagePath = imageDownloader?.CachedImagePath ?? Path.Combine(KnownPaths.ApplianceDir, "debian-base.qcow2");
            if (imageTool is not null)
            {
                await imageTool.CreateCopyAsync(baseImagePath, StagingPath, ct);
                await imageTool.ResizeAsync(StagingPath, 32, ct);
            }
            else
            {
                File.Copy(baseImagePath, StagingPath, overwrite: true);
            }

            // 8. TLS material
            if (!certStore.IsInitialized()) certStore.GenerateCertificates();
            var clientCert = certStore.LoadClientCert();
            var caCert     = certStore.LoadCaCert();
            var accel      = AcceleratorPolicy.Resolve();

            int serialPort = EphemeralPort.Allocate();
            int qmpPort    = EphemeralPort.Allocate();
            int qgaPort    = EphemeralPort.Allocate();

            var args = new QemuArguments()
                .VmName("serpy-build")
                .Machine().Accelerator(accel).Cpu(accel)
                .Smp(settings.CpuCores).Memory(settings.MemoryMb).Headless()
                .FirmwareDir(runtimeResolver?.ShareDir ?? AppContext.BaseDirectory)
                .SystemDisk(StagingPath).Cdrom(SeedIsoPath)
                .TransportDisk(TransportTarPath)
                .ScratchDisk(ScratchDiskPath)
                .UserNetRestricted(settings.ErpNextPort)
                .TlsCredsX509("tls-serial", certStore.QemuCertDir)
                .TlsCredsX509("tls-qmp",    certStore.QemuCertDir)
                .TlsCredsX509("tls-qga",    certStore.QemuCertDir)
                .TlsChardev("serial0", serialPort, "tls-serial")
                .SerialOnChardev("serial0")
                .TlsChardev("qmp0",    qmpPort,    "tls-qmp")
                .TlsChardev("qga0",    qgaPort,    "tls-qga")
                .QmpOnChardev("qmp0")
                .VirtioSerialDevice().QgaVirtioPort("qga0");

            // 9. Boot. The process ends with Serpy, so no orphan can hold the staging disk (KTD4).
            Report("provision", "Booting provisioning VM…", 25);
            var qemuExe = runtimeResolver?.QemuSystemExe ?? "qemu-system-x86_64";
            await using var proc = startProcess is not null
                ? startProcess(qemuExe, args.Args, logPath, log, true)
                : QemuProcess.Start(
                    qemuExe, args.Args, logPath, log, endWithSerpy: true);

            // 10. Watch the guest console. The monitor fails fast on a failure line, a closed
            //     console, a QEMU exit or a guest that never starts (KTD2).
            Report("provision", "Starting the installation inside the virtual computer…", 30);
            IAsyncEnumerable<string> lineStream;
            IAsyncDisposable? serialDisposable = null;
            if (connectSerialLines is not null)
            {
                lineStream = await connectSerialLines(serialPort, clientCert, caCert, ct);
            }
            else
            {
                var serial = await SerialClient.ConnectAsync(
                    "127.0.0.1", serialPort, clientCert, caCert, ct);
                serialDisposable = serial;
                lineStream = serial.ReadLines(ct);
            }

            try
            {
                var monitor = new ProvisionMonitor(ProvisionStartWindow, ProvisionQuietTime, ProvisionTimeout);
                monitor.LineReceived += line =>
                {
                    log.Guest(line);
                    progress.Report(new OperationUpdate(operationId, opKind, "provision",
                        UpdateSeverity.Info, line, null, line + "\n"));
                };
                monitor.StageChanged += stage => Report("provision", $"Installation step: {stage}", null);
                monitor.Stuck += () => log.Info("No output from the guest for " + ProvisionQuietTime.TotalMinutes + " minutes; still waiting.");

                var outcome = await monitor.WaitAsync(lineStream, proc.Exited, ct);
                if (outcome.Code == FailureCode.Cancelled) ct.ThrowIfCancellationRequested();
                if (!outcome.Succeeded)
                {
                    await KillAndClean(proc, ct);
                    RemoveStaging();
                    return Fail(operationId, opKind, outcome.Message, logPath, outcome.Code);
                }
            }
            finally
            {
                if (serialDisposable is not null)
                    await serialDisposable.DisposeAsync();
            }

            // 11. QGA version gate (R9)
            Report("version-gate", "Querying installed versions…", 85);
            GuestVersionReport versions;
            if (queryVersions is not null)
            {
                versions = await queryVersions(qgaPort, clientCert, caCert, ct);
            }
            else
            {
                await using var qga = await QgaClient.ConnectAsync(
                    "127.0.0.1", qgaPort, clientCert, caCert, ct);
                versions = await QueryVersionsAsync(qga, ct);
            }

            Report("version-gate",
                $"python={versions.Python} node={versions.Node} " +
                $"mariadb={versions.MariaDb} redis={versions.Redis} " +
                $"frappe={versions.Frappe} erpnext={versions.ErpNext}", 88);

            try { VersionGate.Validate(versions, manifest); }
            catch (VersionGateException ex)
            {
                await KillAndClean(proc, ct);
                RemoveStaging();
                return Fail(operationId, opKind,
                    $"Version gate: component={ex.Component} installed={ex.Installed} " +
                    $"locked={ex.Locked} floor={ex.Floor}", logPath, FailureCode.ProvisioningFailed);
            }

            // 12. Cloud-init keeps the VM running; the host owns its authenticated shutdown.
            if (sendPowerdown is not null)
            {
                await sendPowerdown(qmpPort, clientCert, caCert, ct);
            }
            else
            {
                await using var qmp = await QmpClient.ConnectAsync(
                    "127.0.0.1", qmpPort, clientCert, caCert, ct);
                await qmp.SendPowerdownAsync(ct);
                if (!await qmp.WaitForShutdownEventAsync(TimeSpan.FromMinutes(3), ct))
                    proc.Kill();
            }
            if (!await proc.WaitForExitAsync(TimeSpan.FromMinutes(2), ct))
                proc.Kill();

            if (File.Exists(FinalPath))
            {
                var backupPath = FinalPath + ".bak";
                File.Replace(StagingPath, FinalPath, backupPath, ignoreMetadataErrors: true);
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            else
            {
                File.Move(StagingPath, FinalPath);
            }

            var accepted = new SystemImageManifest
            {
                BuildTimestamp = DateTimeOffset.UtcNow,
                ImageSha256 = ComputeSha256(FinalPath),
                Versions = versions,
            };
            File.WriteAllText(ManifestPath,
                JsonSerializer.Serialize(accepted,
                    ApplianceStateJsonContext.Default.SystemImageManifest));

            RecordAcceptedBuild(stateStore, FinalPath, preserveData);

            Report("done",
                $"system.qcow2 built. Python={versions.Python} " +
                $"Node={versions.Node} MariaDB={versions.MariaDb} Redis={versions.Redis}", 100);
            return new OperationResult(operationId, opKind,
                OperationOutcome.Success,
                "Build complete — system.qcow2 accepted.", logPath);
        }
        catch (OperationCanceledException)
        {
            RemoveStaging();
            return new OperationResult(operationId, opKind,
                OperationOutcome.Cancelled, "Build cancelled.", logPath, FailureCode.Cancelled);
        }
        catch (Exception ex)
        {
            log.Error("Build failed", ex);
            RemoveStaging();
            return Fail(operationId, opKind, $"Build failed: {ex.Message}", logPath, ClassifyFailure(ex));
        }
        finally
        {
            CacheTransport.Delete(TransportTarPath);
            DeleteIfPresent(ScratchDiskPath);
            DeleteIfPresent(SeedIsoPath);
        }
    }

    /// <summary>
    /// Publishes the durable lifecycle transition only after the final image and
    /// its acceptance manifest have both been written (F1 → F2).
    /// </summary>
    public static void RecordAcceptedBuild(StateStore stateStore, string systemImagePath, bool preserveData = false) =>
        stateStore.Mutate(s =>
        {
            bool hasCommittedData = File.Exists(Path.Combine(KnownPaths.ApplianceDir, "data.img")) ||
                                   File.Exists(Path.Combine(KnownPaths.ApplianceDir, ".data-committed")) ||
                                   !string.IsNullOrEmpty(s.DataImagePath);

            s.Readiness = hasCommittedData ? ReadinessState.Initialized : ReadinessState.Built;
            s.Health = HealthState.Stopped;
            s.SystemImagePath = systemImagePath;
            if (!hasCommittedData)
            {
                s.DataImagePath = null;
            }
            s.QemuPid = null;
            s.QemuStartTimeTicks = null;
            s.QmpPort = null;
            s.QgaPort = null;
            s.SerialPort = null;
            s.LoopbackUrl = null;
            s.ActiveOperation = null;
            s.RecoveryJournal = null;
        });

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BuildInputs TryLoadBuildInputs()
    {
        try { return BuildInputsLoader.Load(); }
        catch { return new BuildInputs(); }
    }

    private static long GetAvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

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
            // The tools print banners. The gate and the replacement check compare bare versions.
            Python  = GuestVersionParser.Python(await Run("python3.14", ["--version"])),
            Node    = GuestVersionParser.Node(await Run("node", ["--version"])),
            MariaDb = GuestVersionParser.MariaDb(await Run("mariadb", ["--version"])),
            Redis   = GuestVersionParser.Redis(await Run("redis-server", ["--version"])),
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

    // The cause is often wrapped (the preflight wraps a failed request), so walk the chain.
    private static FailureCode ClassifyFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case CacheTransportChecksumMismatchException:
                    return FailureCode.ChecksumMismatch;
                case HttpRequestException:
                    return FailureCode.NetworkUnavailable;
                case IOException io when io.HResult == unchecked((int)0x80070070):
                    return FailureCode.DiskFull;
                case TimeoutException:
                    return FailureCode.GuestConsoleClosed;
            }
        }

        return FailureCode.Unknown;
    }

    private static OperationResult Fail(Guid id, OperationKind kind, string msg, string log,
        FailureCode code = FailureCode.Unknown) =>
        new(id, kind, OperationOutcome.Failure, msg, log, code);
}
