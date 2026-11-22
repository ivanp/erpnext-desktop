using System.Net;
using System.Net.Sockets;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;

namespace Serpy.Core.Qemu;

/// <summary>
/// Probes port availability on 127.0.0.1 before spawning QEMU with host port forwarding.
/// If Serpy's own appliance is already running for this workspace, it is not reported as a conflict.
/// </summary>
public static class PortProbe
{
    /// <summary>
    /// Checks whether the port on 127.0.0.1 can be bound.
    /// Returns true if available, false if another program is listening.
    /// </summary>
    public static bool CanBind(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            if (OperatingSystem.IsWindows())
            {
                listener.ExclusiveAddressUse = true;
            }
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies that 127.0.0.1:<paramref name="port"/> is available for ERPNext forwarding.
    /// Returns a failed <see cref="OperationResult"/> with <see cref="FailureCode.PortInUse"/>
    /// if the port is busy by a foreign program, or null if available or held by this workspace's running VM.
    /// </summary>
    public static OperationResult? CheckErpNextPort(
        int port,
        StateStore stateStore,
        Guid operationId,
        OperationKind kind,
        string? logPath = null)
    {
        var state = stateStore.Read();
        // If Serpy's own appliance QEMU is running for this workspace, do not report conflict.
        if (state.QemuPid is { } pid && ProcessIdentity.IsAlive(pid, state.QemuStartTimeTicks ?? 0))
        {
            return null;
        }

        if (!CanBind(port))
        {
            var message = $"Port {port} is already used by another program on this computer. Close the conflicting program or change the port in settings before running.";
            return new OperationResult(
                operationId,
                kind,
                OperationOutcome.Failure,
                message,
                logPath,
                FailureCode.PortInUse);
        }

        return null;
    }
}
