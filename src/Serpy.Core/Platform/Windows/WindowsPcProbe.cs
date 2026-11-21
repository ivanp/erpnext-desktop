using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serpy.Core.Setup;

namespace Serpy.Core.Platform.Windows;

/// <summary>
/// Reads this PC's facts without elevation. It uses the Win32 calls that Windows itself uses, so no
/// WMI, no PowerShell and no reflection are involved, and the app stays Native-AOT compatible.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPcProbe : IPcProbe
{
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(8);

    // PF_VIRT_FIRMWARE_ENABLED. It reports the firmware flag, and it reads false while a hypervisor
    // runs, which is why the evaluator reads it only without one. Measured on the reference PC: false
    // with a hypervisor running, the same as WMI Win32_Processor.VirtualizationFirmwareEnabled.
    private const uint PfVirtFirmwareEnabled = 21;

    // WHvCapabilityCodeHypervisorPresent. WinHvPlatform.dll is the Windows Hypervisor Platform API;
    // it is absent while the Windows feature is off, and then no hypervisor can be running for Serpy.
    private const int WHvCapabilityCodeHypervisorPresent = 0;

    public async Task<PcFacts> ReadAsync(string dataDirectory, IReadOnlyList<Uri> hostsToReach, CancellationToken ct)
    {
        var internet = hostsToReach.Count == 0
            ? InternetFacts.NotNeeded
            : new InternetFacts(true, await ReachAsync(hostsToReach, ct).ConfigureAwait(false));

        return new PcFacts(
            WindowsBuild: Environment.OSVersion.Version.Build,
            TotalMemoryBytes: TotalMemory(),
            FreeDiskBytes: FreeBytes(dataDirectory),
            DataDrive: Path.GetPathRoot(Path.GetFullPath(dataDirectory)) ?? dataDirectory,
            HypervisorRunning: HypervisorRunning(),
            WindowsFeatureInstalled: File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "WinHvPlatform.dll")),
            FirmwareVirtualization: IsProcessorFeaturePresent(PfVirtFirmwareEnabled),
            Internet: internet);
    }

    private static long TotalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0;
    }

    // A path that does not exist yet still has a drive: walk up to the nearest folder that does.
    private static long FreeBytes(string directory)
    {
        var path = Path.GetFullPath(directory);
        while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent) path = parent;
        return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
    }

    private static bool HypervisorRunning()
    {
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            return WHvGetCapability(WHvCapabilityCodeHypervisorPresent, buffer, 64, out _) == 0 &&
                   Marshal.ReadInt32(buffer) != 0;
        }
        catch (DllNotFoundException) { return false; } // the Windows feature is off
        catch (EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool? IsProcessorFeaturePresent(uint feature)
    {
        try { return IsProcessorFeaturePresentNative(feature); }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static async Task<IReadOnlyList<HostReachability>> ReachAsync(IReadOnlyList<Uri> uris, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = HostTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Serpy-PcCheck/1.0");
        var tasks = uris.Select(u => u.Host).Distinct(StringComparer.OrdinalIgnoreCase).Select(async host =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                // Any answer proves the site is reachable, even an error page.
                return new HostReachability(host, true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                return new HostReachability(host, false);
            }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("WinHvPlatform.dll")]
    private static extern int WHvGetCapability(int code, IntPtr buffer, uint size, out uint written);

    [DllImport("kernel32.dll", EntryPoint = "IsProcessorFeaturePresent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresentNative(uint feature);
}
