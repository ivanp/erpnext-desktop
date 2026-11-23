using System.Net;
using System.Runtime.Versioning;
using Serpy.Core.Platform.Windows;
using Serpy.Core.Setup;
using Xunit;

namespace Serpy.Core.Tests.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPcProbeTests
{
    [Fact]
    public async Task ReachAsync_FirstAttemptTimesOut_SecondAttemptSucceeds_HostReachable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var attempts = 0;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Task.Delay(Timeout.Infinite, ct).ContinueWith<HttpResponseMessage>(_ => throw new TimeoutException(), ct);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var probe = new WindowsPcProbe(
            handler,
            attemptTimeouts: [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200)],
            retryDelay: TimeSpan.FromMilliseconds(10),
            delay: (_, _) => Task.CompletedTask);

        var results = await probe.ReachAsync([new Uri("https://archive.mariadb.org/")], CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("archive.mariadb.org", results[0].Host);
        Assert.True(results[0].Reachable);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ReachAsync_AllAttemptsFail_HostUnreachable_FinishesQuickly()
    {
        if (!OperatingSystem.IsWindows()) return;

        var attempts = 0;
        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            attempts++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused"));
        });

        var probe = new WindowsPcProbe(
            handler,
            attemptTimeouts: [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)],
            retryDelay: TimeSpan.FromMilliseconds(10),
            delay: (_, _) => Task.CompletedTask);

        using var safetyCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var results = await probe.ReachAsync([new Uri("https://dead.host.internal/")], safetyCts.Token);

        Assert.Single(results);
        Assert.Equal("dead.host.internal", results[0].Host);
        Assert.False(results[0].Reachable);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ReachAsync_Http404_CountsAsReachable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var handler = new MockHttpMessageHandler((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var probe = new WindowsPcProbe(
            handler,
            attemptTimeouts: [TimeSpan.FromMilliseconds(100)],
            delay: (_, _) => Task.CompletedTask);

        var results = await probe.ReachAsync([new Uri("https://example.com/missing")], CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Reachable);
    }

    [Fact]
    public async Task ReachAsync_Cancellation_ThrowsOperationCanceledException_NotUnreachable()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var cts = new CancellationTokenSource();
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, cts.Token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var probe = new WindowsPcProbe(
            handler,
            attemptTimeouts: [TimeSpan.FromSeconds(5)],
            delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ReachAsync([new Uri("https://example.com/")], cts.Token));
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this((req, _) => handler(req)) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
