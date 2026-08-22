using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Qemu;

public sealed class QemuArgumentsTests
{
    private static readonly AcceleratorResult WhpxAccel =
        new(AcceleratorKind.Whpx, "none", "threads");

    [Fact]
    public void Accelerator_SetsWhpxArg()
    {
        var args = new QemuArguments().Accelerator(WhpxAccel);
        AssertContainsSequence(args.Args, "-accel", "whpx");
    }

    [Fact]
    public void DataDisk_SetsCorrectCacheAndAio()
    {
        var args = new QemuArguments().DataDisk("/data/data.img", WhpxAccel);
        var driveArg = args.Args.First(a => a.Contains("data.img"));
        Assert.Contains("cache=none", driveArg);
        Assert.Contains("aio=threads", driveArg);
        Assert.Contains("format=raw", driveArg);
    }

    [Fact]
    public void TlsChardev_ContainsTlsCredsRef()
    {
        var args = new QemuArguments().TlsChardev("qmp0", 51843, "tls-qmp");
        var chardevArg = args.Args.First(a => a.Contains("qmp0"));
        Assert.Contains("tls-creds=tls-qmp", chardevArg);
        Assert.Contains("port=51843", chardevArg);
        Assert.Contains("server=on", chardevArg);
    }

    [Fact]
    public void SerialOnChardev_AttachesTlsSocketToGuestUart()
    {
        var args = new QemuArguments().TlsChardev("serial0", 51844, "tls-serial")
            .SerialOnChardev("serial0");

        AssertContainsSequence(args.Args, "-serial", "chardev:serial0");
    }

    [Fact]
    public void TlsCredsX509_ContainsVerifyPeer()
    {
        var args = new QemuArguments().TlsCredsX509("tls-qmp", "/certs/qemu");
        var objArg = args.Args.First(a => a.Contains("tls-creds-x509"));
        Assert.Contains("verify-peer=yes", objArg);
        Assert.Contains("endpoint=server", objArg);
        Assert.Contains("dir=/certs/qemu", objArg);
    }

    [Fact]
    public void VmName_Appears_InArgs()
    {
        var args = new QemuArguments().VmName("serpy-gen-42");
        AssertContainsSequence(args.Args, "-name", "serpy-gen-42");
    }

    [Fact]
    public void Headless_AddsNographic()
    {
        var args = new QemuArguments().Headless();
        Assert.Contains("-nographic", args.Args);
    }

    [Fact]
    public void BuilderIsChainable()
    {
        var args = new QemuArguments()
            .Machine()
            .Accelerator(WhpxAccel)
            .Smp(2)
            .Memory(4096)
            .Headless();

        Assert.Contains("-machine", args.Args);
        Assert.Contains("-accel", args.Args);
        Assert.Contains("-nographic", args.Args);
    }

    private static void AssertContainsSequence(IReadOnlyList<string> args, string a, string b)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == a && args[i + 1] == b) return;
        }
        Assert.Fail($"Expected '{a}' followed by '{b}' in args: [{string.Join(", ", args)}]");
    }
}
