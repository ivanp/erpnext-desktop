using System.Diagnostics;
using System.Runtime.Versioning;

namespace Serpy.Core.Qemu;

/// <summary>
/// Detects Windows Hypervisor Platform status and enables it via UAC elevation.
/// The application itself never runs elevated; only this one-shot setup action elevates.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WhpxEnabler
{
    /// <summary>
    /// Non-elevated check: WinHvPlatform.dll present indicates the feature is
    /// installed. The definitive runtime check remains WhpxProbe.RunAsync() —
    /// call it after this returns true to confirm QEMU can actually use WHPX.
    /// </summary>
    public static bool IsLikelyEnabled() =>
        File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WinHvPlatform.dll"));

    /// <summary>
    /// Launch an elevated PowerShell to enable HypervisorPlatform and
    /// VirtualMachinePlatform. Returns true if UAC was accepted; false if declined.
    /// The caller must prompt the user to restart after this returns true.
    /// </summary>
    public static bool EnableAndRequestRestart()
    {
        const string script =
            "Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All -NoRestart; " +
            "Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform -All -NoRestart";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
        };

        try
        {
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined UAC.
            return false;
        }
    }
}
