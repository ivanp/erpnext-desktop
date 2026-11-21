using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Serpy.Core.Contracts;
using Serpy.Core.Guest;

namespace Serpy.Core.Tests.Guest;

public sealed class ProvisionMonitorTests
{
    private sealed class FakeClock
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;
        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class Rig
    {
        public FakeClock Clock { get; } = new();
        public Channel<string> Console { get; } = Channel.CreateUnbounded<string>();
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cts { get; } = new();
        public ProvisionMonitor Monitor { get; }
        public List<string> Stages { get; } = [];
        public int StuckCount;
        private int _seen;

        public Rig()
        {
            Monitor = new ProvisionMonitor(
                startWindow: TimeSpan.FromMinutes(5),
                quietTime: TimeSpan.FromMinutes(10),
                totalTimeout: TimeSpan.FromHours(2),
                clock: () => Clock.Now,
                pollInterval: TimeSpan.FromMilliseconds(5),
                exitGrace: TimeSpan.FromSeconds(1));
            Monitor.StageChanged += s => { lock (Stages) Stages.Add(s); };
            Monitor.Stuck += () => Interlocked.Increment(ref StuckCount);
            Monitor.LineReceived += _ => Interlocked.Increment(ref _seen);
        }

        public Task<ProvisionOutcome> Run() =>
            Monitor.WaitAsync(Lines(), Exit.Task, Cts.Token);

        // The monitor cancels a pending read through the enumerator token before it disposes.
        private async IAsyncEnumerable<string> Lines([EnumeratorCancellation] CancellationToken token = default)
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(token, Cts.Token);
            while (await Console.Reader.WaitToReadAsync(both.Token).ConfigureAwait(false))
                while (Console.Reader.TryRead(out var line))
                    yield return line;
        }

        /// <summary>Waits until the monitor has read and processed <paramref name="count"/> lines.</summary>
        public async Task WaitForLines(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref _seen) < count && DateTime.UtcNow < deadline) await Task.Delay(5);
            Assert.True(Volatile.Read(ref _seen) >= count, "The monitor did not read the lines in time.");
        }

        public void Say(string line) => Console.Writer.TryWrite(line);
    }

    private static async Task<ProvisionOutcome> Finish(Task<ProvisionOutcome> run)
    {
        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == run, "The monitor did not finish within 10 seconds.");
        return await run;
    }

    [Fact]
    public async Task StartedStagesAndDone_GiveSuccessAndOneEventPerStage()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("[    1.000000] Linux version 6.12");
        rig.Say("SERPY_PROVISION_STARTED");
        rig.Say("SERPY_STAGE os-packages");
        rig.Say("Get:1 https://snapshot.debian.org trixie InRelease");
        rig.Say("SERPY_STAGE language-tools");
        rig.Say("SERPY_PROVISION_DONE");

        var outcome = await Finish(run);

        Assert.True(outcome.Succeeded);
        Assert.Equal(FailureCode.None, outcome.Code);
        Assert.Equal(["os-packages", "language-tools"], rig.Stages);
    }

    [Fact]
    public async Task NoStartedLineWithinTheStartWindow_GivesTheNotStartedCode()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("[    1.000000] Linux version 6.12");
        rig.Clock.Advance(TimeSpan.FromMinutes(6));

        var outcome = await Finish(run);

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureCode.ProvisioningNotStarted, outcome.Code);
    }

    [Fact]
    public async Task FailedLine_GivesProvisioningFailedWithStageAndExitCode()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("SERPY_PROVISION_STARTED");
        rig.Say("SERPY_STAGE database");
        rig.Say("E: Unable to locate package mariadb-server");
        rig.Say("SERPY_PROVISION_FAILED database 100");

        var outcome = await Finish(run);

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureCode.ProvisioningFailed, outcome.Code);
        Assert.Equal("database", outcome.Stage);
        Assert.Equal(100, outcome.GuestExitCode);
    }

    [Fact]
    public async Task ConsoleThatClosesBeforeDone_GivesConsoleClosedPromptly()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("SERPY_PROVISION_STARTED");
        rig.Console.Writer.Complete();

        var outcome = await Finish(run);

        Assert.Equal(FailureCode.GuestConsoleClosed, outcome.Code);
    }

    [Fact]
    public async Task ProcessExit_GivesAFailureThatCarriesTheExitCode()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("SERPY_PROVISION_STARTED");
        rig.Exit.SetResult(4);

        var outcome = await Finish(run);

        Assert.False(outcome.Succeeded);
        Assert.Equal(4, outcome.GuestExitCode);
    }

    [Fact]
    public async Task QuietConsole_RaisesStuckOnce_AndTheWaitContinuesToSuccess()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("SERPY_PROVISION_STARTED");
        await rig.WaitForLines(1);
        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref rig.StuckCount) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref rig.StuckCount));
        Assert.False(run.IsCompleted);

        rig.Say("SERPY_PROVISION_DONE");

        Assert.True((await Finish(run)).Succeeded);
    }

    [Fact]
    public async Task Cancellation_GivesTheCancelledCode()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("SERPY_PROVISION_STARTED");
        rig.Cts.Cancel();

        var outcome = await Finish(run);

        Assert.Equal(FailureCode.Cancelled, outcome.Code);
    }

    [Fact]
    public async Task NoDoneLineWithinTheTotalTimeout_Fails()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("SERPY_PROVISION_STARTED");
        await rig.WaitForLines(1); // the started line must be read first, or the start window fires instead
        rig.Clock.Advance(TimeSpan.FromHours(3));

        var outcome = await Finish(run);

        Assert.Equal(FailureCode.ProvisioningFailed, outcome.Code);
        Assert.Contains("allowed time", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedLineThatArrivesJustAfterTheProcessExit_KeepsItsStageAndCode()
    {
        var rig = new Rig();
        var run = rig.Run();

        rig.Say("SERPY_PROVISION_STARTED");
        rig.Say("SERPY_STAGE services");
        await rig.WaitForLines(2);
        rig.Exit.SetResult(1);
        await Task.Delay(20); // the failed marker is still on its way when the exit is seen
        rig.Say("SERPY_PROVISION_FAILED services 5");

        var outcome = await Finish(run);

        Assert.Equal(FailureCode.ProvisioningFailed, outcome.Code);
        Assert.Equal("services", outcome.Stage);
        Assert.Equal(5, outcome.GuestExitCode);
    }

    [Fact]
    public async Task LinesThatExplainAFailure_AreStillDeliveredAfterTheFailedMarker()
    {
        var rig = new Rig();
        var received = new List<string>();
        rig.Monitor.LineReceived += l => { lock (received) received.Add(l); };
        var run = rig.Run();

        rig.Say("SERPY_PROVISION_STARTED");
        rig.Say("SERPY_STAGE database");
        rig.Say("SERPY_PROVISION_FAILED database 100");
        rig.Say("The following packages have unmet dependencies:"); // apt prints this after its header

        var outcome = await Finish(run);

        Assert.Equal("database", outcome.Stage);
        Assert.Equal(100, outcome.GuestExitCode);
        lock (received) Assert.Contains("The following packages have unmet dependencies:", received);
    }
}
