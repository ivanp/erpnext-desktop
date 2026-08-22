using System.Net;
using System.Net.Sockets;

namespace Serpy.Core.Qemu;

/// <summary>
/// Allocates OS-assigned ephemeral TCP ports on loopback.
/// Bind → record → close; QEMU binds the same port immediately after.
/// The lifecycle lock prevents a second instance racing on the same port.
/// </summary>
public static class EphemeralPort
{
    public static int Allocate()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
