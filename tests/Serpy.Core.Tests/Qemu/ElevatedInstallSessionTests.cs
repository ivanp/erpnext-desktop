using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Serpy.Core.Qemu;
using Xunit;

namespace Serpy.Core.Tests.Qemu;

/// <summary>
/// Exercises <see cref="ElevatedInstallSession"/> against a hand-rolled fake
/// pipe SERVER speaking the same wire protocol as the real elevated helper
/// (<c>Serpy.InstallerBootstrapper.exe</c>), without spawning or elevating
/// any real process. Proves the disconnect/timeout/replay/lifecycle
/// properties the client class is responsible for.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedInstallSessionTests
{
    [Fact]
    public async Task FinalizeAsync_CalledTwice_ThrowsInvalidOperationException_WithoutTouchingPipeOnSecondCall()
    {
        var pipeName = $"serpy-session-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = RunFakeServerAsync(server, respondToFinalize: true);

        await using var session = await ElevatedInstallChannel.ConnectAsync(
            pipeName, "n1", "C:\\fake\\installer.exe", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(0, session.StagedResult.ExitCode);

        var first = await session.FinalizeAsync(approve: true, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(0, first.ExitCode);

        // The wire protocol is single-shot -- a second call must be rejected
        // as a programming error before it ever touches the (now server-side
        // closed) pipe, not hang or surface a confusing low-level I/O fault.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.FinalizeAsync(approve: true, TimeSpan.FromSeconds(5), CancellationToken.None));

        await serverTask;
    }

    [Fact]
    public async Task FinalizeAsync_ServerNeverResponds_ThrowsOnTimeout()
    {
        var pipeName = $"serpy-session-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        // Server accepts the connection and sends the staged result, then
        // reads (and discards) the finalize message but deliberately never
        // writes a response -- simulating a hung/crashed helper mid-finalize.
        var serverTask = RunFakeServerAsync(server, respondToFinalize: false);

        await using var session = await ElevatedInstallChannel.ConnectAsync(
            pipeName, "n1", "C:\\fake\\installer.exe", TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => session.FinalizeAsync(approve: true, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        await serverTask;
    }

    [Fact]
    public async Task ConnectAsync_ServerDisconnectsBeforeStagedResult_ThrowsEndOfStream()
    {
        var pipeName = $"serpy-session-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            // Close immediately without ever writing a staged result --
            // simulating the helper crashing right after accepting the
            // connection, before it can report anything.
            server.Disconnect();
        });

        await Assert.ThrowsAsync<IOException>(() => ElevatedInstallChannel.ConnectAsync(
            pipeName, "n1", "C:\\fake\\installer.exe", TimeSpan.FromSeconds(5), CancellationToken.None));

        await serverTask;
    }

    [Fact]
    public async Task DisposeAsync_WaitsForOwningProcessExit_ThenLeavesItExited()
    {
        var pipeName = $"serpy-session-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = RunFakeServerAsync(server, respondToFinalize: true);

        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "exit 0" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var session = await ElevatedInstallChannel.ConnectAsync(
            pipeName, "n1", "C:\\fake\\installer.exe", TimeSpan.FromSeconds(5), CancellationToken.None, proc);
        await session.FinalizeAsync(approve: true, TimeSpan.FromSeconds(5), CancellationToken.None);

        // DisposeAsync must not throw even though it also waits on the real
        // (short-lived) process it was handed, and must complete promptly
        // rather than blocking for its full 15-second bound. Ownership of
        // `proc` (including its final Dispose) belongs to the session from
        // here on -- asserting HasExited on it afterward would itself be
        // invalid, since a disposed Process no longer tracks state.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"DisposeAsync took {sw.Elapsed}.");

        await serverTask;
    }

    /// <summary>
    /// Minimal fake helper server: accepts one connection, reads the hello
    /// line (ignored -- this is testing the client, not authentication),
    /// writes a successful staged result, then reads one finalize line and
    /// (optionally) writes a successful final result.
    /// </summary>
    private static async Task RunFakeServerAsync(NamedPipeServerStream server, bool respondToFinalize)
    {
        await server.WaitForConnectionAsync();
        await ReadLineAsync(server);

        var staged = new BootstrapWireResult(0, "C:\\staged\\dir", null);
        await WriteLineAsync(server, JsonSerializer.Serialize(staged, BootstrapWireJsonContext.Default.BootstrapWireResult));

        await ReadLineAsync(server);
        if (!respondToFinalize) return;

        var final = new BootstrapWireResult(0, "C:\\staged\\dir", null);
        await WriteLineAsync(server, JsonSerializer.Serialize(final, BootstrapWireJsonContext.Default.BootstrapWireResult));
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single);
            if (read == 0) throw new EndOfStreamException();
            if (single[0] == (byte)'\n') break;
            buffer.Add(single[0]);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task WriteLineAsync(Stream stream, string line)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await stream.FlushAsync();
    }
}
