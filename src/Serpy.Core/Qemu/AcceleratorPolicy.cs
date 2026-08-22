using System.Runtime.InteropServices;

namespace Serpy.Core.Qemu;

public enum AcceleratorKind { Whpx, Kvm, Hvf }

public sealed record AcceleratorResult(
    AcceleratorKind Accelerator,
    string DataDiskCache,   // e.g. "none"
    string DataDiskAio);    // e.g. "threads"

/// <summary>
/// Pure, table-driven accelerator selection.
/// (host OS, host arch, guest arch) → AcceleratorResult, or throws for unsupported combos.
/// Never falls back to TCG; mismatched arch is a hard rejection.
/// </summary>
public static class AcceleratorPolicy
{
    private const string GuestArch = "x86_64";

    /// <summary>
    /// Resolve accelerator from runtime host identity.
    /// Throws <see cref="UnsupportedHostException"/> for unsupported combinations.
    /// </summary>
    public static AcceleratorResult Resolve()
    {
        var hostArch = RuntimeInformation.ProcessArchitecture;
        if (hostArch != Architecture.X64)
            throw new UnsupportedHostException(
                $"x86_64 guest requires an x86_64 host; detected host architecture: {hostArch}. " +
                $"TCG software emulation is not accepted for ERPNext.");

        if (OperatingSystem.IsWindows())
            return new AcceleratorResult(AcceleratorKind.Whpx, "none", "threads");

        if (OperatingSystem.IsLinux())
            return new AcceleratorResult(AcceleratorKind.Kvm, "none", "threads");

        if (OperatingSystem.IsMacOS())
            return new AcceleratorResult(AcceleratorKind.Hvf, "writethrough", "threads");

        throw new UnsupportedHostException(
            $"No accelerator policy for OS: {RuntimeInformation.OSDescription}");
    }

    /// <summary>
    /// QEMU -accel argument string (e.g. "whpx", "kvm", "hvf").
    /// </summary>
    public static string AccelArg(AcceleratorKind kind) => kind switch
    {
        AcceleratorKind.Whpx => "whpx",
        AcceleratorKind.Kvm  => "kvm",
        AcceleratorKind.Hvf  => "hvf",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed class UnsupportedHostException(string message) : Exception(message);
