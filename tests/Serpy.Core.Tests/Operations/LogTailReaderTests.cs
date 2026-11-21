using System.Text;
using Serpy.Core.Operations;

namespace Serpy.Core.Tests.Operations;

public sealed class LogTailReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyTail-{Guid.NewGuid():N}");

    public LogTailReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string LogPath => Path.Combine(_dir, "setup.log");

    [Fact]
    public void LinesAppendedAfterTheReaderOpened_ArriveInOrder_AndEachOnlyOnce()
    {
        using var log = new OperationLog(LogPath);
        var reader = new LogTailReader(LogPath);

        log.Info("one");
        var first = reader.ReadNew();
        log.Info("two");
        log.Info("three");
        var second = reader.ReadNew();

        Assert.Single(first);
        Assert.EndsWith("one", first[0], StringComparison.Ordinal);
        Assert.Equal(2, second.Count);
        Assert.EndsWith("two", second[0], StringComparison.Ordinal);
        Assert.EndsWith("three", second[1], StringComparison.Ordinal);
        Assert.Empty(reader.ReadNew());
        Assert.Equal(3, reader.LinesRead);
    }

    [Fact]
    public void AFileThatDoesNotExistYet_YieldsNothing_ThenYieldsItsLinesOnceItAppears()
    {
        var reader = new LogTailReader(LogPath);

        Assert.Empty(reader.ReadNew());
        Assert.Empty(reader.ReadNew());

        using var log = new OperationLog(LogPath);
        log.Info("first line");

        var lines = reader.ReadNew();
        Assert.Single(lines);
        Assert.EndsWith("first line", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ALineStillBeingWritten_IsHeldBack_UntilItsNewlineArrives()
    {
        File.WriteAllText(LogPath, "complete\npart");
        var reader = new LogTailReader(LogPath);

        Assert.Equal(["complete"], reader.ReadNew());

        File.AppendAllText(LogPath, "ial\nnext\n");
        Assert.Equal(["partial", "next"], reader.ReadNew());
    }

    [Fact]
    public void AMultiByteCharacterSplitAcrossTwoWrites_IsNotBroken()
    {
        var bytes = Encoding.UTF8.GetBytes("price: 5 \u20ac\n"); // the euro sign is three bytes
        var split = Array.IndexOf(bytes, (byte)0xE2) + 1;
        using (var stream = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            stream.Write(bytes, 0, split);
        var reader = new LogTailReader(LogPath);

        Assert.Empty(reader.ReadNew());

        using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            stream.Write(bytes, split, bytes.Length - split);

        Assert.Equal(["price: 5 \u20ac"], reader.ReadNew());
    }

    [Fact]
    public void WindowsLineEndings_AreTrimmed()
    {
        File.WriteAllText(LogPath, "a\r\nb\r\n");

        Assert.Equal(["a", "b"], new LogTailReader(LogPath).ReadNew());
    }

    [Fact]
    public void AFileReplacedByAShorterOne_IsReadAgainFromItsStart()
    {
        File.WriteAllText(LogPath, "old line that is quite long\nanother old line\n");
        var reader = new LogTailReader(LogPath);
        reader.ReadNew();

        File.WriteAllText(LogPath, "new\n");

        Assert.Equal(["new"], reader.ReadNew());
    }

    [Fact]
    public void ALogThatIsStillOpenForWriting_CanBeRead()
    {
        using var log = new OperationLog(LogPath);
        log.Info("written while open");

        var lines = new LogTailReader(LogPath).ReadNew();

        Assert.Single(lines);
    }
}
