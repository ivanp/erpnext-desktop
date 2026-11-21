using Serpy.Core.Contracts;

namespace Serpy.Core.Guest;

/// <summary>Result of watching a provisioning run.</summary>
public sealed record ProvisionOutcome(
    bool Succeeded,
    FailureCode Code,
    string Message,
    string? Stage = null,
    int? GuestExitCode = null);

/// <summary>
/// Reads the guest's serial console and decides how provisioning ended (KTD2). It holds all the
/// timing and marker logic so that tests need no QEMU: the line source, the clock and the
/// process-exit signal are injected.
/// </summary>
public sealed class ProvisionMonitor(
    TimeSpan startWindow,
    TimeSpan quietTime,
    TimeSpan totalTimeout,
    Func<DateTimeOffset>? clock = null,
    TimeSpan? pollInterval = null,
    TimeSpan? exitGrace = null)
{
    public const string StartedMarker = "SERPY_PROVISION_STARTED";
    public const string StageMarker = "SERPY_STAGE";
    public const string DoneMarker = "SERPY_PROVISION_DONE";
    public const string FailedMarker = "SERPY_PROVISION_FAILED";

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromMilliseconds(250);

    // After QEMU exits, the last console lines can still be in flight. Wait this long for them.
    private readonly TimeSpan _exitGrace = exitGrace ?? TimeSpan.FromSeconds(2);

    /// <summary>Raised for each stage marker, with the stage id.</summary>
    public event Action<string>? StageChanged;

    /// <summary>Raised once per quiet period when no line arrived for the quiet time; the wait continues.</summary>
    public event Action? Stuck;

    /// <summary>Raised for every console line, after it was read and before it is interpreted.</summary>
    public event Action<string>? LineReceived;

    /// <summary>
    /// Watches <paramref name="lines"/> until provisioning ends. <paramref name="lines"/> yields one
    /// entry per console line, ends when the console closes, and must honour the cancellation token
    /// that is passed to <c>GetAsyncEnumerator</c>: the monitor cancels a read that is still pending
    /// before it disposes the enumerator. <paramref name="processExited"/> completes with the QEMU
    /// exit code when the process ends.
    /// </summary>
    public async Task<ProvisionOutcome> WaitAsync(
        IAsyncEnumerable<string> lines,
        Task<int> processExited,
        CancellationToken ct)
    {
        var begun = _clock();
        var lastActivity = begun;
        var started = false;
        var stuckRaised = false;
        string? stage = null;

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var e = lines.GetAsyncEnumerator(readCts.Token);
        var next = e.MoveNextAsync().AsTask();

        // Takes one console line and returns the outcome when that line ends the provisioning.
        ProvisionOutcome? Interpret(string line)
        {
            lastActivity = _clock();
            stuckRaised = false;
            LineReceived?.Invoke(line);

            if (line.Contains(StartedMarker, StringComparison.Ordinal)) started = true;
            else if (TryStage(line, out var parsedStage)) { stage = parsedStage; StageChanged?.Invoke(parsedStage); }
            else if (line.Contains(DoneMarker, StringComparison.Ordinal))
                return new ProvisionOutcome(true, FailureCode.None, "Provisioning finished.", stage);
            else if (TryFailed(line, out var failedStage, out var code))
                return new ProvisionOutcome(false, FailureCode.ProvisioningFailed,
                    $"Provisioning failed in stage '{failedStage}' with exit code {code}.", failedStage, code);
            return null;
        }

        try
        {
            while (true)
            {
                using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tick = Task.Delay(_poll, tickCts.Token);
                var done = await Task.WhenAny(next, processExited, tick);
                tickCts.Cancel();
                if (ct.IsCancellationRequested)
                    return new ProvisionOutcome(false, FailureCode.Cancelled, "Provisioning was cancelled.", stage);

                if (done == next)
                {
                    bool more;
                    try { more = await next; }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        return new ProvisionOutcome(false, FailureCode.GuestConsoleClosed,
                            $"The guest console connection ended: {ex.Message}", stage);
                    }
                    if (!more)
                        return new ProvisionOutcome(false, FailureCode.GuestConsoleClosed,
                            "The guest console closed before provisioning finished.", stage);

                    var outcome = Interpret(e.Current);
                    if (outcome is not null) return outcome;

                    next = e.MoveNextAsync().AsTask();
                    continue;
                }

                if (done == processExited)
                {
                    var exit = await processExited;

                    // A failed marker that was printed just before the exit may not have been read yet.
                    // Give the console a short time to deliver it, so the stage and code are not lost.
                    using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var grace = Task.Delay(_exitGrace, graceCts.Token);
                    try
                    {
                        while (await Task.WhenAny(next, grace) == next)
                        {
                            bool more;
                            try { more = await next; }
                            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                            {
                                break;
                            }
                            if (!more) break;

                            var late = Interpret(e.Current);
                            if (late is not null) return late;

                            next = e.MoveNextAsync().AsTask();
                        }
                    }
                    finally
                    {
                        graceCts.Cancel();
                    }

                    if (ct.IsCancellationRequested)
                        return new ProvisionOutcome(false, FailureCode.Cancelled, "Provisioning was cancelled.", stage);

                    return new ProvisionOutcome(false, FailureCode.ProvisioningFailed,
                        $"The virtual computer stopped unexpectedly (exit code {exit}).", stage, exit);
                }

                var now = _clock();
                if (!started && now - begun >= startWindow)
                    return new ProvisionOutcome(false, FailureCode.ProvisioningNotStarted,
                        "The guest never reported that provisioning started.", stage);
                if (now - begun >= totalTimeout)
                    return new ProvisionOutcome(false, FailureCode.ProvisioningFailed,
                        "Provisioning did not finish within the allowed time.", stage);
                if (!stuckRaised && started && now - lastActivity >= quietTime)
                {
                    stuckRaised = true;
                    Stuck?.Invoke();
                }
            }
        }
        finally
        {
            // An async iterator cannot be disposed while a read is pending, and most exits above
            // happen with one pending. Cancel it, let it settle, then dispose.
            readCts.Cancel();
            try { await next.ConfigureAwait(false); }
            catch (Exception) { /* the read was cancelled or the console broke; the outcome is decided */ }
            await e.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool TryStage(string line, out string stage)
    {
        stage = "";
        var i = line.IndexOf(StageMarker + " ", StringComparison.Ordinal);
        if (i < 0) return false;
        stage = line[(i + StageMarker.Length + 1)..].Trim();
        return stage.Length > 0;
    }

    private static bool TryFailed(string line, out string stage, out int code)
    {
        stage = "unknown"; code = 1;
        var i = line.IndexOf(FailedMarker, StringComparison.Ordinal);
        if (i < 0) return false;
        var parts = line[(i + FailedMarker.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1) stage = parts[0];
        if (parts.Length >= 2 && int.TryParse(parts[1], out var c)) code = c;
        return true;
    }
}
