using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Health;

namespace Serpy.Core.Operations;

/// <summary>
/// Completely reset an existing Serpy appliance (uninstall / clean reinstall).
/// Sequence:
///   1. If a QEMU VM is running, call StopOperation (or force terminate).
///   2. Delete appliance instance disks: system.qcow2, data.img, and intermediate markers.
///   3. Explicitly PRESERVE base image (debian-base-*.qcow2) and runtime binaries.
///   4. Clear stored health credentials (DPAPI).
///   5. Reset StateStore to NotBuilt / Stopped.
///   6. Delete setup-state.json.
/// </summary>
public sealed class ResetOperation(
    StopOperation stopOp,
    HealthCredentials healthCredentials,
    StateStore stateStore,
    SetupStateStore setupStateStore)
{
    public async Task<OperationResult> ExecuteAsync(
        Guid operationId,
        IProgress<OperationUpdate> progress,
        CancellationToken ct)
    {
        var logPath = Path.Combine(KnownPaths.LogsDir, $"reset-{operationId:N}.log");
        void Report(string stage, string msg) =>
            progress.Report(new OperationUpdate(
                operationId, OperationKind.Reset, stage,
                UpdateSeverity.Info, msg, null, null));

        Report("stopping", "Checking for and stopping running appliance…");
        try
        {
            await stopOp.ExecuteAsync(operationId, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report("stopping-warning", $"Stop reported: {ex.Message}; continuing cleanup.");
        }

        // Also check if any QEMU process is still lingering by PID in stateStore
        var state = stateStore.Read();
        if (state.QemuPid is { } pid &&
            ProcessIdentity.IsAlive(pid, state.QemuStartTimeTicks ?? 0))
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
        }

        Report("wiping-disks", "Removing appliance disk images and markers…");
        var applianceDir = KnownPaths.ApplianceDir;
        if (Directory.Exists(applianceDir))
        {
            string[] diskFilesToDelete =
            [
                "system.qcow2",
                ".staging-system.qcow2",
                "data.img",
                ".pending-data.img",
                ".data-committed",
                "seed.iso",
                "recovery.journal",
            ];

            foreach (var fileName in diskFilesToDelete)
            {
                var target = Path.Combine(applianceDir, fileName);
                if (File.Exists(target))
                {
                    try { File.Delete(target); }
                    catch (Exception ex)
                    {
                        Report("delete-warning", $"Could not delete {fileName}: {ex.Message}");
                    }
                }
            }
        }

        Report("wiping-credentials", "Clearing appliance credentials…");
        try
        {
            healthCredentials.Clear();
        }
        catch (Exception ex)
        {
            Report("cred-warning", $"Could not clear credentials: {ex.Message}");
        }

        Report("resetting-state", "Resetting appliance lifecycle state…");
        stateStore.Write(new ApplianceState
        {
            Readiness = ReadinessState.NotBuilt,
            Health = HealthState.Stopped,
            QemuPid = null,
            QemuStartTimeTicks = null,
            QmpPort = null,
            QgaPort = null,
            SerialPort = null,
            LoopbackUrl = null,
            ActiveOperation = null,
            SystemImagePath = null,
            DataImagePath = null,
            LogPath = null,
            RecoveryJournal = null,
            AdoptionJournal = null,
        });
        try
        {
            setupStateStore.Write(new SetupState());
        }
        catch (Exception ex)
        {
            Report("setup-warning", $"Could not reset setup state: {ex.Message}");
        }

        Report("done", "Appliance reset complete.");
        return new OperationResult(
            operationId, OperationKind.Reset, OperationOutcome.Success,
            "Appliance successfully reset to unbuilt state.", logPath);
    }
}
