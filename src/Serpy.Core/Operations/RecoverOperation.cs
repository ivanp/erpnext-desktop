using System.Text.Json;
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
using Serpy.Core.Versions;

namespace Serpy.Core.Operations;

/// <summary>
/// F4: Replace system.qcow2 while retaining data.img (KD5, R6).
///
/// Sequence:
///   1. Validate replacement: acceptance manifest present, no backing_file.
///   2. Read new version markers; reject any downgrade.
///   3. Write recovery journal before any mutation.
///   4. Swap system.qcow2 → replacement (journalled).
///   5. Boot replacement + data.img; run recover.sh (mariadb-upgrade + bench migrate).
///   6. R11 health check.
///   7. Graceful powerdown; clear journal; update state.
/// A normal start is blocked while the journal is open (KTD8).
/// </summary>
public sealed class RecoverOperation(
    QemuImageTool imageTool,
    ManagedRuntimeResolver runtimeResolver,
    TlsCertificateStore certStore,
    HealthCredentials healthCredentials,
    StateStore stateStore,
    VersionManifest manifest,
    ApplianceSettings settings)
{
    private string SystemImagePath  => Path.Combine(KnownPaths.ApplianceDir, "system.qcow2");
    private string SystemBackupPath => Path.Combine(KnownPaths.ApplianceDir, ".system.qcow2.recovering");

    public async Task<OperationResult> ExecuteAsync(
        Guid operationId,
        string replacementImagePath,
        IProgress<OperationUpdate> progress,
        CancellationToken ct)
    {
        var logPath = Path.Combine(KnownPaths.LogsDir, $"recover-{operationId:N}.log");
        Directory.CreateDirectory(KnownPaths.LogsDir);

        void Report(string stage, string msg, int? pct = null) =>
            progress.Report(new OperationUpdate(
                operationId, OperationKind.Recover, stage,
                UpdateSeverity.Info, msg, pct, null));

        // 1. Validate replacement.
        Report("validate", "Validating replacement image…", 5);
        if (!File.Exists(replacementImagePath))
            return Fail(operationId, $"Replacement image not found: {replacementImagePath}", logPath);

        if (IsCurrentSystemImageReplacement(SystemImagePath, replacementImagePath))
            return Fail(operationId,
                "Replacement image must be a distinct file from the current system image.", logPath);

        var acceptancePath = Path.Combine(
            Path.GetDirectoryName(replacementImagePath)!,
            SystemImageManifest.FileName);
        if (!File.Exists(acceptancePath))
            return Fail(operationId,
                "Replacement image has no Serpy acceptance manifest (.system-accepted.json). " +
                "Only images produced by Serpy Build are accepted.", logPath);

        var backing = await imageTool.GetBackingFileAsync(replacementImagePath, ct);
        if (!string.IsNullOrEmpty(backing))
            return Fail(operationId,
                $"Replacement image has a backing_file chain ({backing}). " +
                "Standalone qcow2 images only.", logPath);

        var newManifest = JsonSerializer.Deserialize(
            File.ReadAllText(acceptancePath),
            ApplianceStateJsonContext.Default.SystemImageManifest);
        if (newManifest is null)
            return Fail(operationId, "Cannot parse replacement image manifest.", logPath);
        if (!newManifest.MatchesImageDigest(replacementImagePath))
            return Fail(operationId,
                "Replacement image does not match its Serpy acceptance manifest digest.", logPath);

        // 2. Reject static-floor downgrades before mutation.
        try { VersionGate.ValidateNoAppDowngrade(newManifest.Versions, manifest); }
        catch (VersionGateException ex)
        {
            return Fail(operationId,
                $"Downgrade rejected: {ex.Component} installed={ex.Installed} floor={ex.Floor}.", logPath);
        }
        if (!VersionEvaluator.MeetsFloor(
                newManifest.Versions.MariaDb, manifest.Runtime.MariaDb.Floor))
            return Fail(operationId,
                $"Downgrade rejected: replacement MariaDB {newManifest.Versions.MariaDb} " +
                $"is below the minimum floor {manifest.Runtime.MariaDb.Floor}.", logPath);

        var state = stateStore.Read();
        var journalTargetsReplacement = state.RecoveryJournal is { TargetImagePath: var target } &&
            string.Equals(target, replacementImagePath, StringComparison.Ordinal);
        var systemImageExists = File.Exists(SystemImagePath);
        var backupExists = File.Exists(SystemBackupPath);
        var interruptedSwapAwaitingCopy = IsInterruptedSwapAwaitingCopy(
            journalTargetsReplacement, systemImageExists, backupExists,
            state.RecoveryJournal?.DiskSwapped ?? false);
        var currentMatchesReplacement = newManifest.MatchesImageDigest(SystemImagePath);
        var postCopyBeforeJournal = journalTargetsReplacement &&
            IsCompletedCopyAwaitingJournal(
                currentMatchesReplacement,
                state.RecoveryJournal!.SourceImageSha256,
                newManifest.ImageSha256,
                backupExists,
                state.RecoveryJournal.DiskSwapped);

        if (interruptedSwapAwaitingCopy)
        {
            // The source move completed but the replacement copy did not. The durable
            // journal authenticates the state; step 4 copies the validated replacement.
        }
        else if (journalTargetsReplacement && (state.RecoveryJournal!.DiskSwapped || postCopyBeforeJournal))
        {
            if (!currentMatchesReplacement)
                return Fail(operationId,
                    "Current system image does not match the accepted replacement manifest; " +
                    "recovery cannot continue safely.", logPath);
            if (postCopyBeforeJournal)
            {
                stateStore.Mutate(s => s.RecoveryJournal!.DiskSwapped = true);
                state = stateStore.Read();
            }
        }
        else
        {
            var installedAcceptancePath = Path.Combine(KnownPaths.ApplianceDir, SystemImageManifest.FileName);
            if (!File.Exists(installedAcceptancePath))
                return Fail(operationId,
                    "Current system image has no Serpy acceptance manifest; recovery cannot compare versions safely.", logPath);
            var installedManifest = JsonSerializer.Deserialize(
                File.ReadAllText(installedAcceptancePath),
                ApplianceStateJsonContext.Default.SystemImageManifest);
            if (installedManifest is null || !installedManifest.MatchesImageDigest(SystemImagePath))
                return Fail(operationId,
                    "Current system image does not match its Serpy acceptance manifest; " +
                    "recovery cannot compare versions safely.", logPath);
            try { VersionGate.ValidateReplacementDoesNotDowngrade(newManifest.Versions, installedManifest.Versions); }
            catch (VersionGateException ex)
            {
                return Fail(operationId,
                    $"Downgrade rejected: replacement {ex.Component} {ex.Installed} " +
                    $"is below installed {ex.Floor}.", logPath);
            }
        }

        // 3. Write recovery journal (idempotent: continue if same target).
        if (state.RecoveryJournal is null ||
            state.RecoveryJournal.TargetImagePath != replacementImagePath)
        {
            if (state.RecoveryJournal is { TargetImagePath: not null } j &&
                j.TargetImagePath != replacementImagePath)
                return Fail(operationId,
                    $"An incomplete recovery for a different image is in progress " +
                    $"({j.TargetImagePath}). Finish or clear it before starting a new one.", logPath);

            var installedAcceptancePath = Path.Combine(KnownPaths.ApplianceDir, SystemImageManifest.FileName);
            var installedManifest = JsonSerializer.Deserialize(
                File.ReadAllText(installedAcceptancePath),
                ApplianceStateJsonContext.Default.SystemImageManifest)!;
            Report("journal", "Writing recovery journal…", 10);
            stateStore.Mutate(s => s.RecoveryJournal = new RecoveryJournal
            {
                TargetImagePath      = replacementImagePath,
                SourceImageSha256    = installedManifest.ImageSha256,
                TargetMariaDbVersion = newManifest.Versions.MariaDb,
                // Use actual Frappe version from the acceptance manifest (not a Python proxy).
                TargetFrappeVersion  = newManifest.Versions.Frappe,
            });
            state = stateStore.Read();
        }

        // 4. Swap disk. An interrupted copy leaves only the original backup;
        // preserve it and resume the copy rather than deleting the only known-good image.
        if (!state.RecoveryJournal!.DiskSwapped)
        {
            Report("swap", "Swapping system disk…", 15);
            if (IsSwapInProgress(File.Exists(SystemImagePath), File.Exists(SystemBackupPath)))
            {
                File.Copy(replacementImagePath, SystemImagePath);
            }
            else
            {
                if (File.Exists(SystemBackupPath))
                    return Fail(operationId,
                        "Recovery backup exists while the current system image is still present; " +
                        "refusing to discard the backup.", logPath);
                File.Move(SystemImagePath, SystemBackupPath);
                try
                {
                    File.Copy(replacementImagePath, SystemImagePath);
                }
                catch
                {
                    // Keep the journal open and the original image intact at SystemBackupPath for retry.
                    throw;
                }
            }
            stateStore.Mutate(s => s.RecoveryJournal!.DiskSwapped = true);
        }

        // 5. Boot and migrate.
        Report("boot", "Booting with replacement disk…", 20);
        var accel      = AcceleratorPolicy.Resolve();
        var dataPath   = state.DataImagePath ?? Path.Combine(KnownPaths.ApplianceDir, "data.img");
        int serialPort = EphemeralPort.Allocate();
        int qmpPort    = EphemeralPort.Allocate();
        int qgaPort    = EphemeralPort.Allocate();

        if (!certStore.IsInitialized()) certStore.GenerateCertificates();
        var clientCert = certStore.LoadClientCert();
        var caCert     = certStore.LoadCaCert();

        var args = new QemuArguments()
            .VmName($"serpy-recover-{operationId:N}")
            .Machine("q35").Accelerator(accel).Cpu()
            .Smp(settings.CpuCores).Memory(settings.MemoryMb).Headless()
            .FirmwareDir(runtimeResolver.ShareDir)
            .SystemDisk(SystemImagePath)
            .DataDisk(dataPath, accel)
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

        await using var proc = QemuProcess.Start(runtimeResolver.QemuSystemExe, args.Args, logPath);
        await using var _ = await SerialClient.ConnectAsync(
            "127.0.0.1", serialPort, clientCert, caCert, ct);
        await using var qga = await QgaClient.ConnectAsync(
            "127.0.0.1", qgaPort, clientCert, caCert, ct);
        var guestOps = new GuestOperations(qga);

        if (!state.RecoveryJournal.MariaDbUpgraded)
        {
            Report("migrate", "Running mariadb-upgrade + bench migrate…", 50);
            var migResult = await guestOps.RunRecoverAsync(
                "site1.local", TimeSpan.FromHours(1), ct);
            if (!migResult.Succeeded)
            {
                proc.Kill();
                return Fail(operationId,
                    $"recover.sh failed (exit {migResult.ExitCode}): {migResult.Stderr}", logPath);
            }
            stateStore.Mutate(s =>
            {
                s.RecoveryJournal!.MariaDbUpgraded = true;
                s.RecoveryJournal!.BenchMigrated   = true;
            });
        }

        // 6. Health check (R11).
        if (!state.RecoveryJournal.HealthPassed)
        {
            Report("health", "Running functional health check…", 85);
            var health = await new HealthChecker(
                guestOps, "site1.local",
                $"http://127.0.0.1:{settings.ErpNextPort}",
                healthCredentials).RunAsync(ct);

            if (!health.Healthy)
            {
                proc.Kill();
                return Fail(operationId,

                    $"Post-recovery health check failed [{health.FailedCheck}]: {health.Reason}",
                    logPath);
            }
            stateStore.Mutate(s => s.RecoveryJournal!.HealthPassed = true);
        }

        // 7. Power down through the authenticated monitor; never clear the
        // recovery journal until the process has actually exited.
        Report("powerdown", "Sending graceful powerdown via QMP…", 95);
        await using var qmp = await QmpClient.ConnectAsync(
            "127.0.0.1", qmpPort, clientCert, caCert, ct);
        await qmp.SendPowerdownAsync(ct);
        if (!await qmp.WaitForShutdownEventAsync(TimeSpan.FromMinutes(3), ct))
            return Fail(operationId,
                "QMP did not confirm guest shutdown; recovery journal remains open.", logPath);
        if (!await proc.WaitForExitAsync(TimeSpan.FromMinutes(2), ct))
            return Fail(operationId,
                "QEMU did not exit after graceful shutdown; recovery journal remains open.", logPath);
        // Publish the acceptance manifest only after replacement health and shutdown pass.
        // Until then the open recovery journal keeps normal start blocked.
        File.Copy(acceptancePath, Path.Combine(KnownPaths.ApplianceDir, SystemImageManifest.FileName), overwrite: true);


        stateStore.Mutate(s =>
        {
            s.RecoveryJournal = null;
            s.ActiveOperation = null;
            s.SystemImagePath = SystemImagePath;
        });

        if (File.Exists(SystemBackupPath)) File.Delete(SystemBackupPath);

        Report("done", "Recovery complete. Appliance is ready to start.", 100);
        return new OperationResult(operationId, OperationKind.Recover,
            OperationOutcome.Success, "Recovery complete.", logPath);
    }

    public static bool MatchesCurrentImageForRecovery(
        SystemImageManifest installedManifest,
        SystemImageManifest replacementManifest,
        bool diskSwapped,
        string currentImagePath) =>
        (diskSwapped ? replacementManifest : installedManifest).MatchesImageDigest(currentImagePath);

    public static bool IsSwapInProgress(bool systemImageExists, bool backupImageExists) =>
        !systemImageExists && backupImageExists;

    public static bool IsCurrentSystemImageReplacement(
        string currentSystemImagePath, string replacementImagePath) =>
        string.Equals(
            ResolveReparsePointAliases(currentSystemImagePath),
            ResolveReparsePointAliases(replacementImagePath),
            StringComparison.OrdinalIgnoreCase);

    private static string ResolveReparsePointAliases(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var resolved = root;
        var components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < components.Length; index++)
        {
            var candidate = Path.Combine(resolved, components[index]);
            if (Directory.Exists(candidate))
            {
                var directory = new DirectoryInfo(candidate);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                    {
                        resolved = target.FullName;
                        continue;
                    }
                }
            }

            resolved = candidate;
        }

        var file = new FileInfo(resolved);
        if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
            resolved = file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;

        return Path.GetFullPath(resolved);
    }

    public static bool IsCompletedCopyAwaitingJournal(
        bool currentMatchesReplacement, string originalImageSha256,
        string replacementImageSha256, bool backupImageExists, bool diskSwapped) =>
        currentMatchesReplacement && !diskSwapped && backupImageExists &&
        !string.IsNullOrWhiteSpace(originalImageSha256) &&
        !string.Equals(originalImageSha256, replacementImageSha256,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsInterruptedSwapAwaitingCopy(
        bool journalTargetsReplacement, bool systemImageExists,
        bool backupImageExists, bool diskSwapped) =>
        journalTargetsReplacement && !diskSwapped &&
        IsSwapInProgress(systemImageExists, backupImageExists);

    private static OperationResult Fail(Guid id, string msg, string log) =>
        new(id, OperationKind.Recover, OperationOutcome.Failure, msg, log);
}
