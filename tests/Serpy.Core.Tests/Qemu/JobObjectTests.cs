using System.Diagnostics;
using Serpy.Core.Platform.Windows;

namespace Serpy.Core.Tests.Qemu;

public sealed class JobObjectTests
{
    private static Process StartSleeper()
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi)!;
    }

    [Fact]
    public void AChildInTheJob_EndsWhenTheJobHandleCloses()
    {
        if (!OperatingSystem.IsWindows()) return; // a Windows API; Linux and macOS are compile-only targets

        var child = StartSleeper();
        try
        {
            var job = new KillOnCloseJob();
            Assert.True(job.TryAssign(child), "The child could not be assigned to the job.");
            Assert.False(child.HasExited);

            job.Dispose();

            Assert.True(child.WaitForExit(10_000), "The child was still running after the job handle closed.");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.Dispose();
        }
    }

    [Fact]
    public void AChildOutsideTheJob_KeepsRunningWhenTheJobCloses()
    {
        if (!OperatingSystem.IsWindows()) return; // a Windows API; Linux and macOS are compile-only targets

        var inside = StartSleeper();
        var outside = StartSleeper();
        try
        {
            var job = new KillOnCloseJob();
            Assert.True(job.TryAssign(inside));

            job.Dispose();
            inside.WaitForExit(10_000);

            Assert.False(outside.HasExited);
        }
        finally
        {
            if (!inside.HasExited) inside.Kill(entireProcessTree: true);
            if (!outside.HasExited) outside.Kill(entireProcessTree: true);
            inside.Dispose();
            outside.Dispose();
        }
    }

    [Fact]
    public void DisposingTwice_CausesNoError()
    {
        if (!OperatingSystem.IsWindows()) return; // a Windows API; Linux and macOS are compile-only targets

        var job = new KillOnCloseJob();
        job.Dispose();
        job.Dispose();
    }
}
