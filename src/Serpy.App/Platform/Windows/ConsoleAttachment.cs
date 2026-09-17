using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Serpy.App.Platform.Windows;

/// <summary>
/// Attaches Serpy.App (a WinExe) to the parent calling console so CLI output renders in cmd/PowerShell.
/// </summary>
public static class ConsoleAttachment
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// Attempt to attach to parent console. Returns true if attached and standard streams redirected.
    /// Safe on non-Windows (returns false).
    /// </summary>
    public static bool TryAttachParent()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
                return false;

            // Re-bind Console.Out and Console.Error to the attached standard handles
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Console.WriteLine(); // Ensure clean line after terminal prompt
            return true;
        }
        catch
        {
            return false;
        }
    }
}
