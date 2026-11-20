using Serpy.Core.Operations;
using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Qemu;

public sealed class QemuProcessLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyQemuLog-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string ReadShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task StartedProcess_WritesItsOutputStartAndExitIntoTheOperationLog()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "build.log");
        using var log = new OperationLog(path);

        var proc = QemuProcess.Start("cmd.exe", ["/c", "echo hello from the process"], log: log);
        var code = await proc.Exited.WaitAsync(TimeSpan.FromSeconds(20));
        await proc.DisposeAsync();

        var text = ReadShared(path);
        Assert.Equal(0, code);
        Assert.Contains("QEMU started, process id", text, StringComparison.Ordinal);
        Assert.Contains("hello from the process", text, StringComparison.Ordinal);
        Assert.Contains("QEMU exited with code 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitedTask_ReportsTheExitCodeOfAFailingProcess()
    {
        if (!OperatingSystem.IsWindows()) return;

        var proc = QemuProcess.Start("cmd.exe", ["/c", "exit 7"]);
        var code = await proc.Exited.WaitAsync(TimeSpan.FromSeconds(20));
        await proc.DisposeAsync();

        Assert.Equal(7, code);
    }

    [Fact]
    public async Task OutputThatArrivesAfterTheLogIsDisposed_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_dir, "late.log");
        var log = new OperationLog(path);

        var proc = QemuProcess.Start("cmd.exe", ["/c", "ping -n 2 127.0.0.1 > nul & echo late output"], log: log);
        log.Dispose();
        var code = await proc.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        await proc.DisposeAsync();

        Assert.Equal(0, code);
    }
}
