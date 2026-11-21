namespace Serpy.Core.Guest;

/// <summary>
/// Reads the guest console in the background and hands each line to a callback, until it is
/// disposed. An operation that starts the VM with a TLS serial chardev has to do this: nothing
/// draining the connection lets its buffers fill, and the guest then blocks on its own console
/// writes, which can stall the boot before the guest agent starts. The lines also go to the
/// operation log, so a guest that never comes up leaves its boot output behind.
/// </summary>
public sealed class ConsolePump : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _reading;
    private int _disposed;

    public ConsolePump(IAsyncEnumerable<string> lines, Action<string> onLine, CancellationToken ct = default)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reading = Task.Run(() => ReadAsync(lines, onLine, _stop.Token));
    }

    private static async Task ReadAsync(IAsyncEnumerable<string> lines, Action<string> onLine, CancellationToken ct)
    {
        try
        {
            await foreach (var line in lines.WithCancellation(ct).ConfigureAwait(false))
            {
                // A log write that fails must not end the pump: the next line may still be written.
                try { onLine(line); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The operation ended, or the guest closed the console. Neither is a problem for the caller.
        }
    }

    /// <summary>True once the console has closed or the pump was stopped.</summary>
    public bool Completed => _reading.IsCompleted;

    public async ValueTask DisposeAsync()
    {
        // Disposing twice is allowed (an await using plus an explicit call does it), and only the first counts.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _reading.ConfigureAwait(false); }
        catch (Exception) { /* ReadAsync handles its own end; nothing more to report */ }
        _stop.Dispose();
    }
}
