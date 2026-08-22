using System.ComponentModel;
using System.Diagnostics;

namespace Serpy.Core.Coordination;

/// <summary>
/// Matches a live OS process against the PID and start-time recorded in state.
/// Prevents trusting a recycled PID from a later unrelated process.
/// </summary>
public static class ProcessIdentity
{
    /// <summary>
    /// Returns true only if a process with <paramref name="pid"/> is alive
    /// and its start time matches <paramref name="startTimeTicks"/> within a
    /// one-second tolerance (OS time resolution varies).
    /// </summary>
    public static bool IsAlive(int pid, long startTimeTicks)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            var delta = Math.Abs(proc.StartTime.ToUniversalTime().Ticks - startTimeTicks);
            return delta < TimeSpan.FromSeconds(1).Ticks;
        }
        catch (ArgumentException)
        {
            // PID not found.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process exited between GetProcessById and StartTime access.
            return false;
        }
        catch (Win32Exception)
        {
            // Access denied querying a system process (e.g. PID 0, PID 4 on Windows).
            // Cannot verify identity → treat as not matching.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Capture the start-time ticks for a freshly spawned process.</summary>
    public static long StartTimeTicks(Process proc) =>
        proc.StartTime.ToUniversalTime().Ticks;
}
