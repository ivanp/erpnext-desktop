using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Protocols.Qmp;
using Serpy.Core.Qemu;

namespace Serpy.Core.Operations;

/// <summary>
/// Graceful QMP ACPI powerdown (R13, R7).
/// Sequence:
///   1. Connect QMP with mTLS (registers event reader before system_powerdown).
///   2. Send system_powerdown.
///   3. Wait for SHUTDOWN event or process exit.
///   4. Report clean success only after confirmed halt.
///   5. On timeout: send QMP quit, then kill process; report hard-kill.
/// </summary>
public sealed class StopOperation(
    StateStore stateStore,
    TlsCertificateStore certStore)
{
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(60);

    public async Task<OperationResult> ExecuteAsync(
        Guid operationId,
        IProgress<OperationUpdate> progress,
        CancellationToken ct)
    {
        var logPath = Path.Combine(KnownPaths.LogsDir, $"stop-{operationId:N}.log");

        void Report(string stage, string msg) =>
            progress.Report(new OperationUpdate(
                operationId, OperationKind.Stop, stage,
                UpdateSeverity.Info, msg, null, null));

        var state = stateStore.Read();

        if (state.QemuPid is not { } pid || state.QmpPort is not { } qmpPort)
        {
            stateStore.Mutate(s => s.Health = HealthState.Stopped);
            return new OperationResult(operationId, OperationKind.Stop,
                OperationOutcome.Success, "No running QEMU process recorded.", logPath);
        }

        Report("connect", "Opening QMP connection…");
        var clientCert = certStore.LoadClientCert();
        var caCert     = certStore.LoadCaCert();

        QmpClient? qmp = null;
        try
        {
            qmp = await QmpClient.ConnectAsync(
                "127.0.0.1", qmpPort, clientCert, caCert, ct);

            Report("powerdown", "Sending ACPI power-down…");
            await qmp.SendPowerdownAsync(ct);

            Report("waiting", "Waiting for clean shutdown…");
            var clean = await qmp.WaitForShutdownEventAsync(GracefulTimeout, ct);

            if (clean)
            {
                stateStore.Mutate(s =>
                {
                    s.Health = HealthState.Stopped;
                    s.QemuPid = null;
                    s.QmpPort = null;
                    s.QgaPort = null;
                    s.SerialPort = null;
                    s.LoopbackUrl = null;
                });
                return new OperationResult(operationId, OperationKind.Stop,
                    OperationOutcome.Success, "Appliance shut down cleanly.", logPath);
            }

            // Timeout path: QMP quit then kill.
            Report("timeout", "Graceful shutdown timed out — sending QMP quit…");
            try { await qmp.ExecuteAsync("quit", ct: ct); } catch { /* ignore */ }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report("error", $"QMP error: {ex.Message}");
        }
        finally
        {
            if (qmp is not null) await qmp.DisposeAsync();
        }

        // Hard kill.
        if (Coordination.ProcessIdentity.IsAlive(pid,
            state.QemuStartTimeTicks ?? 0))
        {
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
        }

        stateStore.Mutate(s =>
        {
            s.Health = HealthState.Stopped;
            s.QemuPid = null;
            s.QmpPort = null;
            s.QgaPort = null;
            s.SerialPort = null;
            s.LoopbackUrl = null;
        });

        return new OperationResult(operationId, OperationKind.Stop,
            OperationOutcome.Success,
            "Appliance stopped (hard kill — graceful shutdown timed out).", logPath);
    }
}
