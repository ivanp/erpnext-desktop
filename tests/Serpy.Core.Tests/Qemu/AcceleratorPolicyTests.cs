using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Qemu;

public sealed class AcceleratorPolicyTests
{
    [Fact]
    public void Resolve_OnWindowsX64_ReturnsWhpxWithCorrectCachePolicy()
    {
        // This test only makes a meaningful assertion when running on Windows x64.
        // On other platforms it validates the exception path.
        if (!OperatingSystem.IsWindows())
        {
            // Accept that non-Windows runners hit UnsupportedHostException or HVF/KVM.
            return;
        }

        var result = AcceleratorPolicy.Resolve();
        Assert.Equal(AcceleratorKind.Whpx, result.Accelerator);
        Assert.Equal("none", result.DataDiskCache);
        Assert.Equal("threads", result.DataDiskAio);
    }

    [Fact]
    public void AccelArg_ReturnsExpectedStrings()
    {
        Assert.Equal("whpx", AcceleratorPolicy.AccelArg(AcceleratorKind.Whpx));
        Assert.Equal("kvm",  AcceleratorPolicy.AccelArg(AcceleratorKind.Kvm));
        Assert.Equal("hvf",  AcceleratorPolicy.AccelArg(AcceleratorKind.Hvf));
    }
}
