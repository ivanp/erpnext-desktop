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
///
/// The handoff is two-phase (IR3), not single-shot: after the helper's
/// initial copy/re-verify/launch/reparse-scan sequence, the staging tree is
/// handed back READ-ONLY to the requesting user -- granting write access
/// immediately would let anything running under that same user's SID mutate
/// the tree while the caller is still validating it. Only after the caller
/// reports its own validation outcome via <see cref="ElevatedInstallSession.FinalizeAsync"/>
/// does the helper grant full ownership/write access (or delete the tree, on
/// a declined/failed validation).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedInstallChannel
{
    /// <summary>
    /// Connect to the elevated helper's named pipe and send the nonce +
    /// installer path. Returns a still-open <see cref="ElevatedInstallSession"/>
    /// whose <see cref="ElevatedInstallSession.StagedResult"/> reports the
    /// outcome of the helper's initial sequence. If that result's
    /// <c>ExitCode</c> is nonzero, the helper has already sent its terminal
    /// message and closed -- the caller must NOT call
    /// <see cref="ElevatedInstallSession.FinalizeAsync"/> in that case. Throws
    /// <see cref="TimeoutException"/>-style pipe exceptions if the helper
    /// never accepts a connection within <paramref name="connectTimeout"/>
    /// (e.g. it crashed or was never started), and
    /// <see cref="InvalidDataException"/> if its response cannot be parsed.
    /// </summary>
    public static async Task<ElevatedInstallSession> ConnectAsync(
        string pipeName,
        string nonce,
        string installerPath,
        TimeSpan connectTimeout,
        CancellationToken ct,
        System.Diagnostics.Process? elevatedProcess = null)
    {
        var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct);

            var hello = new BootstrapClientHello(nonce, installerPath);
            var helloLine = JsonSerializer.Serialize(hello, BootstrapWireJsonContext.Default.BootstrapClientHello);
            await client.WriteAsync(Encoding.UTF8.GetBytes(helloLine + "\n"), ct);
            await client.FlushAsync(ct);

            var resultLine = await ReadLineAsync(client, ct);
            var result = JsonSerializer.Deserialize(resultLine, BootstrapWireJsonContext.Default.BootstrapWireResult)
                ?? throw new InvalidDataException("Elevated bootstrapper sent an empty or unparseable result.");

            return new ElevatedInstallSession(
                client, nonce, new BootstrapLaunchResult(result.ExitCode, result.StagingDir), elevatedProcess);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    internal static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
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

/// <summary>
/// A still-open connection to the elevated bootstrapper, after its initial
/// copy/re-verify/launch/reparse-scan sequence but before ownership of the
/// staging tree has transferred (IR3). Must be finalized (approved or
/// declined) or disposed; disposing without finalizing leaves the helper's
/// own connection-drop handling to clean up the still-admin-owned staging
/// tree rather than ever granting it write access to an unvalidated result.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedInstallSession(
    NamedPipeClientStream client, string nonce, BootstrapLaunchResult stagedResult,
    System.Diagnostics.Process? elevatedProcess = null)
    : IBootstrapSession
{
    private int _finalizeCalled;


    /// <summary>
    /// Outcome of the helper's initial sequence. When <c>ExitCode</c> is 0,
    /// <c>StagingDir</c> is a tree the requesting user may READ but not
    /// write -- call <see cref="FinalizeAsync"/> after validating it. When
    /// nonzero, the helper has already sent its terminal message and closed;
    /// do not call <see cref="FinalizeAsync"/>.
    /// </summary>
    public BootstrapLaunchResult StagedResult { get; } = stagedResult;

    /// <summary>
    /// Report the caller's own validation outcome for the staged tree.
    /// <paramref name="approve"/> true grants the requesting user full
    /// ownership/write access to the staging tree (only now, after
    /// validation passed); false tells the helper to delete it instead of
    /// ever making it writable. Returns the helper's final result.
    /// </summary>
    public async Task<BootstrapLaunchResult> FinalizeAsync(bool approve, TimeSpan timeout, CancellationToken ct)
    {
        // The wire protocol is strictly single-shot: the helper reads
        // exactly one finalize message and then closes. A second call on
        // the same session would write into an already-closing/closed pipe
        // and hang or throw a confusing low-level I/O error instead of a
        // clear programming-error signal, so reject it up front.
        if (Interlocked.Exchange(ref _finalizeCalled, 1) != 0)
            throw new InvalidOperationException("FinalizeAsync has already been called on this session.");

        var request = new BootstrapFinalizeRequest(nonce, approve);
        var line = JsonSerializer.Serialize(request, BootstrapWireJsonContext.Default.BootstrapFinalizeRequest);
        await client.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        await client.FlushAsync(ct);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var resultLine = await ElevatedInstallChannel.ReadLineAsync(client, linkedCts.Token);
        var result = JsonSerializer.Deserialize(resultLine, BootstrapWireJsonContext.Default.BootstrapWireResult)
            ?? throw new InvalidDataException("Elevated bootstrapper sent an empty or unparseable final result.");

        return new BootstrapLaunchResult(result.ExitCode, result.StagingDir);
    }

    /// <summary>
    /// <see cref="IBootstrapSession"/> explicit-shape overload: the caller's
    /// own validation (ValidateContents/-version/TLS probe) can take real
    /// time, so this uses a generous 90-second default matching the
    /// server-side wait, rather than tying finalize's own network-read
    /// timeout to the caller's possibly much shorter cancellation token.
    /// </summary>
    Task<BootstrapLaunchResult> IBootstrapSession.FinalizeAsync(bool approve, CancellationToken ct) =>
        FinalizeAsync(approve, TimeSpan.FromSeconds(90), ct);

    public async ValueTask DisposeAsync()
    {
        // Close the client half first: this is what causes the elevated
        // helper to observe the connection drop (if the protocol never
        // reached a terminal write) and exit on its own. Only after that
        // do we confirm the process we launched actually goes away --
        // without this, a hung or crashed-post-protocol helper process
        // leaks silently as an orphaned elevated process with nobody
        // watching its exit code.
        await client.DisposeAsync();

        if (elevatedProcess is null) return;
        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await elevatedProcess.WaitForExitAsync(waitCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Best-effort: the wire protocol already completed (or the
            // caller chose not to finalize); a helper that still hasn't
            // exited 15s after its pipe closed is unusual but not this
            // disposal's job to force-kill. Left for process-level
            // observability rather than silently swallowed forever.
        }
        finally
        {
            elevatedProcess.Dispose();
        }
    }
}
