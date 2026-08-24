using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Serpy.Core.Qemu;

/// <summary>
/// App-side (non-elevated) named-pipe <b>client</b> half of the authenticated
/// handoff with the elevated <c>Serpy.InstallerBootstrapper.exe</c> helper
/// (IR3/IKTD1/IU1 step 7). The helper hosts the pipe <b>server</b> — restricted
/// by DACL to the SID this process passed as a launch argument — so it can
/// impersonate the connecting client and read its real token SID rather than
/// trusting anything this class sends on its own.
///
/// This class only ever runs after the elevated helper process has already
/// been started (see <c>ManagedRuntimeResolver.DefaultBootstrapLauncher</c>);
/// it does not itself elevate or launch anything.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedInstallChannel
{
    /// <summary>
    /// Connect to the elevated helper's named pipe, send the nonce + installer
    /// path, and await its final result. Throws <see cref="TimeoutException"/>
    /// if the helper never accepts a connection within <paramref name="connectTimeout"/>
    /// (e.g. it crashed or was never started), and <see cref="InvalidDataException"/>
    /// if the helper's response cannot be parsed.
    /// </summary>
    public static async Task<BootstrapLaunchResult> ConnectAndAwaitResultAsync(
        string pipeName,
        string nonce,
        string installerPath,
        TimeSpan connectTimeout,
        CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await client.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct);

        var hello = new BootstrapClientHello(nonce, installerPath);
        var helloLine = JsonSerializer.Serialize(hello, BootstrapWireJsonContext.Default.BootstrapClientHello);
        var helloBytes = Encoding.UTF8.GetBytes(helloLine + "\n");
        await client.WriteAsync(helloBytes, ct);
        await client.FlushAsync(ct);

        var resultLine = await ReadLineAsync(client, ct);
        var result = JsonSerializer.Deserialize(resultLine, BootstrapWireJsonContext.Default.BootstrapWireResult)
            ?? throw new InvalidDataException("Elevated bootstrapper sent an empty or unparseable result.");

        return new BootstrapLaunchResult(result.ExitCode, result.StagingDir);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, ct);
            if (read == 0)
                throw new EndOfStreamException("Elevated bootstrapper pipe closed before sending a result.");
            if (single[0] == (byte)'\n') break;
            buffer.Add(single[0]);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
