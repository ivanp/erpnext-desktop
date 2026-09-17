using System.Diagnostics;

namespace Serpy.App.Platform;

/// <summary>
/// Manages Serpy single-instance takeover and external process teardown during unattended execution.
/// </summary>
public static class ProcessController
{
    /// <summary>
    /// Attempt to acquire the single-instance mutex.
    /// If already held by another Serpy instance and in unattended mode, gracefully close
    /// or terminate the existing instance, then acquire the mutex.
    /// </summary>
    public static Mutex AcquireOrTakeoverMutex(string mutexName, bool isUnattended, Action<string>? log = null)
    {
        var mutex = new Mutex(false, mutexName);
        bool acquired = false;
        try
        {
            acquired = mutex.WaitOne(isUnattended ? TimeSpan.FromSeconds(1) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (acquired)
            return mutex;

        if (!isUnattended)
        {
            mutex.Dispose();
            throw new InvalidOperationException("Another instance of Serpy.App is already running.");
        }

        log?.Invoke("Existing Serpy.App process detected. Terminating previous instance…");
        var currentProc = Process.GetCurrentProcess();
        string procName = currentProc.ProcessName;

        try
        {
            var running = Process.GetProcessesByName(procName);
            foreach (var p in running)
            {
                if (p.Id == currentProc.Id) continue;
                try
                {
                    if (!p.CloseMainWindow())
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    else
                    {
                        if (!p.WaitForExit(3000))
                            p.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Process may have already exited
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Warning while stopping existing processes: {ex.Message}");
        }

        // Now wait up to 5 seconds to acquire the mutex
        try
        {
            acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            throw new TimeoutException("Failed to acquire single-instance mutex after stopping existing process.");
        }

        return mutex;
    }
}
