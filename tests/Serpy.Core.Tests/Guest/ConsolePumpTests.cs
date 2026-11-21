using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Serpy.Core.Guest;

namespace Serpy.Core.Tests.Guest;

public sealed class ConsolePumpTests
{
    private static async IAsyncEnumerable<string> Source(Channel<string> channel, [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            while (channel.Reader.TryRead(out var line))
                yield return line;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(condition(), "The condition did not become true within 10 seconds.");
    }

    [Fact]
    public async Task EveryConsoleLine_ReachesTheCallbackInOrder_WhileTheOperationDoesOtherWork()
    {
        var channel = Channel.CreateUnbounded<string>();
        var seen = new List<string>();
        await using var pump = new ConsolePump(Source(channel), l => { lock (seen) seen.Add(l); });

        for (var i = 0; i < 50; i++) channel.Writer.TryWrite($"boot line {i}");
        await WaitFor(() => { lock (seen) return seen.Count == 50; });

        lock (seen) Assert.Equal(Enumerable.Range(0, 50).Select(i => $"boot line {i}"), seen);
    }

    [Fact]
    public async Task ADisposedPump_StopsReading_AndDoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<string>();
        var pump = new ConsolePump(Source(channel), _ => { });

        await pump.DisposeAsync();
        await pump.DisposeAsync(); // a second dispose is harmless too

        Assert.True(pump.Completed);
    }

    [Fact]
    public async Task AConsoleThatCloses_EndsThePumpQuietly()
    {
        var channel = Channel.CreateUnbounded<string>();
        await using var pump = new ConsolePump(Source(channel), _ => { });

        channel.Writer.TryWrite("last line");
        channel.Writer.Complete();

        await WaitFor(() => pump.Completed);
    }

    [Fact]
    public async Task ACallbackThatFailsToWrite_DoesNotStopTheLinesAfterIt()
    {
        var channel = Channel.CreateUnbounded<string>();
        var seen = new List<string>();
        await using var pump = new ConsolePump(Source(channel), l =>
        {
            if (l == "poison") throw new IOException("log is locked");
            lock (seen) seen.Add(l);
        });

        channel.Writer.TryWrite("before");
        channel.Writer.TryWrite("poison");
        channel.Writer.TryWrite("after");
        await WaitFor(() => { lock (seen) return seen.Count == 2; });

        lock (seen) Assert.Equal(["before", "after"], seen);
    }

    [Fact]
    public async Task TheCallersCancel_EndsThePump()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var cts = new CancellationTokenSource();
        await using var pump = new ConsolePump(Source(channel), _ => { }, cts.Token);

        await cts.CancelAsync();

        await WaitFor(() => pump.Completed);
    }
}
