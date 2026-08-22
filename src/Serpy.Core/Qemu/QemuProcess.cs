using System.Diagnostics;

namespace Serpy.Core.Qemu;

/// <summary>
/// Wraps a managed QEMU child process.
/// Owns start, exit monitoring, and hard-kill.
/// State reconciliation (PID + start-time identity) is the caller's responsibility.
/// </summary>
public sealed class QemuProcess : IAsyncDisposable
{
    private readonly Process _proc;
    private bool _disposed;

    private QemuProcess(Process proc) => _proc = proc;

    public int Pid => _proc.Id;
    public long StartTimeTicks => _proc.StartTime.ToUniversalTime().Ticks;
    public bool HasExited => _proc.HasExited;
    public int ExitCode => _proc.ExitCode;

    /// <summary>
    /// Start QEMU with the given executable, arguments, and firmware library directory.
    /// Stdout/stderr are captured; no console window is shown.
    /// </summary>
    public static QemuProcess Start(
        string executable,
        IReadOnlyList<string> args,
        string? logFile = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (logFile is not null)
        {
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    File.AppendAllText(logFile, e.Data + "\n");
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    File.AppendAllText(logFile, "[stderr] " + e.Data + "\n");
            };
        }

        proc.Start();

        if (logFile is not null)
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }

        return new QemuProcess(proc);
    }

    /// <summary>
    /// Wait for the process to exit voluntarily (used after QMP shutdown).
    /// </summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _proc.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hard-kill the QEMU process tree. Used after graceful shutdown timeout.
    /// Logs the hard-kill so it is never silent.
    /// </summary>
    public void Kill(bool entireTree = true)
    {
        try { _proc.Kill(entireTree); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_proc.HasExited)
            Kill();

        await _proc.WaitForExitAsync();
        _proc.Dispose();
    }
}
