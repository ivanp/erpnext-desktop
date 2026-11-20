using System.Diagnostics;
using Serpy.Core.Operations;
using Serpy.Core.Platform.Windows;

namespace Serpy.Core.Qemu;

/// <summary>
/// Wraps a managed QEMU child process.
/// Owns start, exit monitoring, and hard-kill.
/// State reconciliation (PID + start-time identity) is the caller's responsibility.
/// </summary>
public sealed class QemuProcess : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly KillOnCloseJob? _job;
    private readonly OperationLog? _log;
    private bool _disposed;

    private QemuProcess(Process proc, KillOnCloseJob? job, OperationLog? log)
    {
        _proc = proc;
        _job = job;
        _log = log;
    }

    public int Pid => _proc.Id;
    public long StartTimeTicks => _proc.StartTime.ToUniversalTime().Ticks;
    public bool HasExited => _proc.HasExited;
    public int ExitCode => _proc.ExitCode;

    /// <summary>Completes with QEMU's exit code when the process ends, for whatever reason.</summary>
    public Task<int> Exited { get; private set; } = Task.FromResult(0);

    /// <summary>
    /// Start QEMU with the given executable and arguments. Stdout and stderr are captured to
    /// <paramref name="log"/> (or to <paramref name="logFile"/> when only a path is given); no
    /// console window is shown. With <paramref name="endWithSerpy"/> the process runs inside a job
    /// object that ends it when Serpy exits (KTD4); the everyday ERPNext process passes false.
    /// </summary>
    public static QemuProcess Start(
        string executable,
        IReadOnlyList<string> args,
        string? logFile = null,
        OperationLog? log = null,
        bool endWithSerpy = false)
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
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) =>
        {
            try { exited.TrySetResult(proc.ExitCode); }
            catch (InvalidOperationException) { exited.TrySetResult(-1); }
        };

        if (log is not null)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log.Info("qemu: " + e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.Info("qemu stderr: " + e.Data); };
        }
        else if (logFile is not null)
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

        KillOnCloseJob? job = null;
        if (endWithSerpy && OperatingSystem.IsWindows())
        {
            job = new KillOnCloseJob();
            if (!job.TryAssign(proc))
            {
                log?.Info("The QEMU process could not be placed in a job; it will not end with Serpy.");
                job.Dispose();
                job = null;
            }
        }

        if (log is not null || logFile is not null)
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }

        log?.Info($"QEMU started, process id {proc.Id}.");
        var qemu = new QemuProcess(proc, job, log) { Exited = exited.Task };
        return qemu;
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
        _log?.Info("QEMU is being ended.");
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
        try { _log?.Info($"QEMU exited with code {_proc.ExitCode}."); }
        catch (InvalidOperationException) { }
        if (OperatingSystem.IsWindows()) _job?.Dispose();
        _proc.Dispose();
    }
}
