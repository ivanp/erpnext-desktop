using System.Diagnostics;

namespace Serpy.Core.Qemu;

/// <summary>
/// Proves WHPX availability by launching the managed QEMU binary with
/// "-accel whpx -machine none" and checking the exit code.
/// Never assumes Hyper-V registry keys or elevated capabilities.
/// Never accepts a TCG success path.
/// </summary>
public static class WhpxProbe
{
    /// <summary>
    /// Run the probe. Returns a result indicating success or the plain-language reason for failure.
    ///
    /// Protocol:
    ///   1. Start QEMU paused (-S) with -accel whpx -machine q35.
    ///   2. Wait up to 5 s for stderr lines — WHPX failures appear during init, before paused state.
    ///   3. If the process is still alive after 5 s, WHPX succeeded (paused = no startup error).
    ///   4. Kill the process; return result.
    ///   Never calls WaitForExitAsync without a timeout because -S never exits on its own.
    /// </summary>
    public static async Task<WhpxProbeResult> RunAsync(
        string qemuSystemExe,
        string firmwareDir,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(qemuSystemExe)
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        // q35 is the minimal valid x86 target for WHPX; -machine none is invalid.
        // -S starts paused so the probe exits via our explicit Kill, never on its own.
        // -nographic suppresses the GTK window.
        foreach (var a in new[] { "-accel", "whpx", "-machine", "q35",
                                   "-L", firmwareDir, "-S", "-nographic" })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Collect stderr for up to 5 s.  WHPX init errors appear immediately;
        // if nothing fatal appears and the process is still alive, WHPX is working.
        var stderrLines = new System.Text.StringBuilder();
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            // Read line-by-line so we stop as soon as the timeout fires.
            while (true)
            {
                var line = await proc.StandardError.ReadLineAsync(readCts.Token);
                if (line is null) break;
                stderrLines.AppendLine(line);
            }
        }
        catch (OperationCanceledException) { /* timeout — expected */ }

        var stderr = stderrLines.ToString();

        // Kill the paused process — it will never exit under -S.
        if (!proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        bool whpxFailed =
            stderr.Contains("WHPX", StringComparison.OrdinalIgnoreCase) &&
            (stderr.Contains("not present",  StringComparison.OrdinalIgnoreCase) ||
             stderr.Contains("unavailable",  StringComparison.OrdinalIgnoreCase) ||
             stderr.Contains("failed to initialise", StringComparison.OrdinalIgnoreCase));

        // Also treat an early unexpected exit (before our Kill) as failure.
        bool earlyExit = proc.HasExited && proc.ExitCode != 0;

        if (whpxFailed || earlyExit)
        {
            return new WhpxProbeResult(
                Success: false,
                Message: "Windows Hypervisor Platform (WHPX) is not available. " +
                         "Enable 'Windows Hypervisor Platform' in Windows Features and reboot. " +
                         $"QEMU output: {stderr.Trim()}");
        }

        return new WhpxProbeResult(Success: true, Message: "WHPX probe passed.");
    }

    /// <summary>
    /// Run qemu-system-x86_64 -version and return the version string, or throw on failure.
    /// </summary>
    public static async Task<string> GetVersionAsync(
        string qemuSystemExe,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(qemuSystemExe, ["-version"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"qemu-system-x86_64 -version failed (exit {proc.ExitCode})");

        return stdout.Trim();
    }
}

public sealed record WhpxProbeResult(bool Success, string Message);
