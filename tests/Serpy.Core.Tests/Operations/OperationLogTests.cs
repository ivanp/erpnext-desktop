using Serpy.Core.Operations;

namespace Serpy.Core.Tests.Operations;

public sealed class OperationLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyLogTest-{Guid.NewGuid():N}");

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
    public void ASecondReader_SeesEachLineWhileTheWriterKeepsTheFileOpen()
    {
        var path = Path.Combine(_dir, "build.log");
        using var log = new OperationLog(path);

        log.Stage("download", "Downloading Debian base image");
        using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            Assert.Contains("Downloading Debian base image", reader.ReadToEnd(), StringComparison.Ordinal);

        log.Guest("SERPY_PROVISION_STARTED");
        using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            Assert.Contains("SERPY_PROVISION_STARTED", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnError_IsWrittenWithTheFullExceptionChain()
    {
        var path = Path.Combine(_dir, "init.log");
        using var log = new OperationLog(path);
        var ex = new InvalidOperationException("outer", new IOException("the inner cause"));

        log.Error("Build failed", ex);

        var text = ReadShared(path);
        Assert.Contains("outer", text, StringComparison.Ordinal);
        Assert.Contains("the inner cause", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingAfterDispose_IsIgnored()
    {
        var path = Path.Combine(_dir, "late.log");
        var log = new OperationLog(path);
        log.Info("first");
        log.Dispose();

        log.Info("late line from a QEMU output event");

        Assert.DoesNotContain("late line", ReadShared(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentWritersThroughOneLog_LoseNoLineAndNeverInterleaveInsideALine()
    {
        // One log per file is the contract: two FileStreams on one file overwrite each other, so
        // QEMU output events and the operation both write through the same instance.
        var path = Path.Combine(_dir, "shared.log");
        using var log = new OperationLog(path);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) log.Guest($"writer {w} line {i}");
        })));

        var lines = ReadShared(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8 * 200, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"^\d\d:\d\d:\d\d  guest      writer \d line \d+$", l.TrimEnd('\r')));
    }

    [Fact]
    public void AMultiLineMessage_GetsAStampOnEveryLine()
    {
        var path = Path.Combine(_dir, "multi.log");
        using var log = new OperationLog(path, () => new DateTimeOffset(2026, 11, 20, 19, 20, 49, TimeSpan.Zero));

        log.Info("line one\nline two");

        var lines = ReadShared(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("19:20:49", l, StringComparison.Ordinal));
    }

    [Fact]
    public void ARegisteredSecret_NeverReachesTheFile_WhateverTextCarriesIt()
    {
        const string secret = "Tr0ub4dor&3-the-admin-password";
        var path = Path.Combine(_dir, "secret.log");
        using var log = new OperationLog(path);
        log.Redact(secret);

        log.Stage("health", $"Signing in as Administrator with {secret}");
        log.Guest($"login failed for {secret}");
        log.Error("Guest command failed", new InvalidOperationException($"stderr echoed {secret} back"));
        log.Info("a message without the secret stays whole");

        var text = ReadShared(path);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.Contains("a message without the secret stays whole", text, StringComparison.Ordinal);
        Assert.Contains("Signing in as Administrator with ********", text, StringComparison.Ordinal);
    }
}
