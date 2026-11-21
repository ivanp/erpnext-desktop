using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;
using Serpy.Core.Setup;

namespace Serpy.Core.Tests.Operations;

public sealed class SetupOperationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpySetupOp-{Guid.NewGuid():N}");

    public SetupOperationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Clock
    {
        private long _ticks = DateTimeOffset.UnixEpoch.Ticks;
        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class FakePower : IPowerRequest
    {
        public int Held;
        public int Released;
        public int Active => Held - Released;

        public IDisposable Hold()
        {
            Interlocked.Increment(ref Held);
            return new Releaser(this);
        }

        private sealed class Releaser(FakePower owner) : IDisposable
        {
            private int _done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Increment(ref owner.Released);
            }
        }
    }

    private sealed class FakeStep(SetupStepId id, int weight = 10) : ISetupStep
    {
        public SetupStepId Id => id;
        public int Weight => weight;
        public TimeSpan QuietTime { get; set; } = TimeSpan.FromMinutes(10);

        public bool Done;
        public int Runs;
        public int LeftoverDeletes;
        public Func<SetupContext, SetupStepResult?>? PreflightWork;
        public Func<SetupContext, CancellationToken, Task<SetupStepResult>> Work =
            (_, _) => Task.FromResult(SetupStepResult.Success());

        public SetupStepResult? Preflight(SetupContext context) => PreflightWork?.Invoke(context);
        public bool IsDone(SetupContext context) => Done;

        public async Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            var result = await Work(context, ct);
            if (result.Outcome == OperationOutcome.Success) Done = true;
            return result;
        }

        public void DeleteLeftovers(SetupContext context) => LeftoverDeletes++;
    }

    private sealed class Collector : IProgress<SetupUpdate>
    {
        public List<SetupUpdate> Updates { get; } = [];
        public void Report(SetupUpdate value) { lock (Updates) Updates.Add(value); }
        public SetupUpdate[] Snapshot() { lock (Updates) return [.. Updates]; }
    }

    private SetupStateStore NewSetupStore() => new(Path.Combine(_dir, "setup-state.json"));

    private StateStore NewStateStore() => new(Path.Combine(_dir, "state.json"));

    private SetupOperation MakeOp(
        IReadOnlyList<ISetupStep> steps,
        FakePower? power = null,
        Clock? clock = null,
        TimeSpan? tick = null,
        Action? reconcile = null,
        StateStore? stateStore = null,
        SetupStateStore? setupStore = null) =>
        new(steps, setupStore ?? NewSetupStore(), stateStore ?? NewStateStore(),
            power ?? new FakePower(), reconcile ?? (() => { }),
            clock: clock is null ? null : () => clock.Now,
            tick: tick ?? TimeSpan.FromMilliseconds(10),
            minEmitGap: TimeSpan.Zero,
            logPathFor: id => Path.Combine(_dir, $"setup-{id:N}.log"));

    private static FakeStep[] AllSteps() =>
        [new(SetupStepId.Build), new(SetupStepId.CompanyData), new(SetupStepId.Start)];

    // ── order, skipping and failure ──────────────────────────────────────────

    [Fact]
    public async Task AFreshState_RunsTheStepsInOrder_AndEndsFinished()
    {
        var order = new List<SetupStepId>();
        var steps = AllSteps();
        foreach (var s in steps)
        {
            var id = s.Id;
            s.Work = (_, _) => { order.Add(id); return Task.FromResult(SetupStepResult.Success()); };
        }

        var setup = NewSetupStore();
        var updates = new Collector();
        var result = await MakeOp(steps, setupStore: setup).RunAsync(new SetupRequest(), updates, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal([SetupStepId.Build, SetupStepId.CompanyData, SetupStepId.Start], order);
        Assert.True(setup.Read().Finished);
        var last = updates.Snapshot()[^1];
        Assert.Equal(SetupRunState.Finished, last.Run);
        Assert.Equal(100, last.OverallPercent);
        Assert.All(last.Steps, s => Assert.Equal(SetupStepState.Done, s.State));
    }

    [Fact]
    public async Task AFailureInBuild_EndsTheRun_MarksBuildFailed_AndSavesTheCode()
    {
        var steps = AllSteps();
        steps[0].Work = (_, _) => Task.FromResult(SetupStepResult.Failed(FailureCode.ProvisioningFailed, "stage database failed"));

        var setup = NewSetupStore();
        var updates = new Collector();
        var result = await MakeOp(steps, setupStore: setup).RunAsync(new SetupRequest(), updates, CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.ProvisioningFailed, result.Code);
        Assert.Equal(SetupStepId.Build, result.FailedStep);
        Assert.Equal(0, steps[1].Runs);
        var state = setup.Read();
        Assert.Equal(FailureCode.ProvisioningFailed, state.LastFailure);
        Assert.Equal(SetupStepId.Build, state.LastFailedStep);
        Assert.Equal(SetupStepState.Failed, state.Steps[SetupStepId.Build]);
        Assert.False(state.Finished);
        var last = updates.Snapshot()[^1];
        Assert.Equal(SetupRunState.Failed, last.Run);
        Assert.Equal(FailureCode.ProvisioningFailed, last.Failure);
    }

    [Fact]
    public async Task TheRunAfterAFailure_StartsBuildAgain_AndRepeatsNoFinishedStep()
    {
        var steps = AllSteps();
        var firstTry = true;
        steps[1].Work = (_, _) => Task.FromResult(firstTry
            ? SetupStepResult.Failed(FailureCode.DataUnusable, "data step failed")
            : SetupStepResult.Success());
        var setup = NewSetupStore();
        var op = MakeOp(steps, setupStore: setup);

        var first = await op.RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);
        Assert.Equal(OperationOutcome.Failure, first.Outcome);
        Assert.Equal(1, steps[0].Runs);

        firstTry = false;
        var second = await op.RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, second.Outcome);
        Assert.Equal(1, steps[0].Runs); // Build finished the first time and its result still exists
        Assert.Equal(2, steps[1].Runs);
        Assert.Equal(FailureCode.None, setup.Read().LastFailure);
    }

    [Fact]
    public async Task AStepWhoseResultExists_IsSkipped_WithoutRunningOrTakingThePowerRequest()
    {
        var steps = AllSteps();
        foreach (var s in steps) s.Done = true;
        var power = new FakePower();

        var result = await MakeOp(steps, power).RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.All(steps, s => Assert.Equal(0, s.Runs));
        Assert.Equal(0, power.Held);
    }

    // ── cancel ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACancelDuringBuild_EndsAsCancelled_AndTheNextRunDeletesLeftoversBeforeItBuilds()
    {
        var steps = AllSteps();
        using var cts = new CancellationTokenSource();
        steps[0].Work = async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return SetupStepResult.Success();
        };
        var op = MakeOp(steps);

        var cancelled = await op.RunAsync(new SetupRequest(), new Collector(), cts.Token);

        Assert.Equal(OperationOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal(FailureCode.Cancelled, cancelled.Code);
        Assert.False(steps[0].Done);

        steps[0].Work = (_, _) => Task.FromResult(SetupStepResult.Success());
        var before = steps[0].LeftoverDeletes;
        var next = await op.RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, next.Outcome);
        Assert.True(steps[0].LeftoverDeletes > before, "leftovers must be deleted before the step runs again");
    }

    [Fact]
    public async Task NoLeftoverIsDeleted_WhileAnAdoptionOrRecoveryJournalIsOpen()
    {
        var stateStore = NewStateStore();
        stateStore.Mutate(s => s.AdoptionJournal = new AdoptionJournal());
        var steps = AllSteps();

        await MakeOp(steps, stateStore: stateStore).RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);

        Assert.All(steps, s => Assert.Equal(0, s.LeftoverDeletes));
    }

    [Fact]
    public async Task Journals_AreReconciled_BeforeAnyStepIsAskedOrDeleted()
    {
        var calls = new List<string>();
        var steps = AllSteps();
        steps[0].Work = (_, _) => { calls.Add("run"); return Task.FromResult(SetupStepResult.Success()); };

        await MakeOp(steps, reconcile: () => calls.Add("reconcile"))
            .RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);

        Assert.Equal("reconcile", calls[0]);
    }

    // ── stuck, keep waiting, try again ───────────────────────────────────────

    [Fact]
    public async Task NoActivityForTheQuietTime_RaisesStuck_AndKeepWaitingClearsItWhileTheStepKeepsWorking()
    {
        var clock = new Clock();
        var steps = AllSteps();
        var release = new TaskCompletionSource();
        steps[0].QuietTime = TimeSpan.FromMinutes(10);
        steps[0].Work = async (_, ct) => { await release.Task.WaitAsync(ct); return SetupStepResult.Success(); };
        var updates = new Collector();
        var op = MakeOp(steps, clock: clock);

        var run = op.RunAsync(new SetupRequest(), updates, CancellationToken.None);
        await WaitFor(() => updates.Snapshot().Any(u => u.CurrentStep == SetupStepId.Build));
        clock.Advance(TimeSpan.FromMinutes(11));
        await WaitFor(() => updates.Snapshot().Any(u => u.Stuck));

        op.KeepWaiting();
        await WaitFor(() => !updates.Snapshot()[^1].Stuck);
        Assert.False(run.IsCompleted); // the step went on working

        clock.Advance(TimeSpan.FromMinutes(11));
        await WaitFor(() => updates.Snapshot()[^1].Stuck); // the timer was re-armed

        release.SetResult();
        Assert.Equal(OperationOutcome.Success, (await run).Outcome);
    }

    [Fact]
    public async Task TryAgain_CancelsTheStep_DeletesItsLeftovers_AndRunsItFromTheStart()
    {
        var steps = AllSteps();
        var attempts = 0;
        var started = new TaskCompletionSource();
        steps[0].Work = async (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return SetupStepResult.Success();
        };
        var op = MakeOp(steps);

        var run = op.RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        op.TryAgainStep();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal(2, steps[0].Runs);
        Assert.True(steps[0].LeftoverDeletes >= 2);
    }

    [Fact]
    public async Task WhileAStepWorks_UpdatesKeepArriving_EvenWhenTheStepPrintsNothing()
    {
        var steps = AllSteps();
        var release = new TaskCompletionSource();
        steps[0].Work = async (_, ct) => { await release.Task.WaitAsync(ct); return SetupStepResult.Success(); };
        var updates = new Collector();

        var run = MakeOp(steps).RunAsync(new SetupRequest(), updates, CancellationToken.None);
        await WaitFor(() => updates.Snapshot().Any(u => u.CurrentStep == SetupStepId.Build));
        var seen = updates.Snapshot().Length;
        await WaitFor(() => updates.Snapshot().Length >= seen + 3); // the tick re-sends the elapsed time

        release.SetResult();
        await run;
        Assert.True(updates.Snapshot().Length >= seen + 3);
    }

    [Fact]
    public async Task AnActivityLine_AndAStepPercent_ReachTheNextUpdate()
    {
        var steps = AllSteps();
        var release = new TaskCompletionSource();
        steps[0].Work = async (context, ct) =>
        {
            context.Activity("Installing the database");
            context.StepPercent(50);
            await release.Task.WaitAsync(ct);
            return SetupStepResult.Success();
        };
        var updates = new Collector();

        var run = MakeOp(steps).RunAsync(new SetupRequest(), updates, CancellationToken.None);
        await WaitFor(() => updates.Snapshot().Any(u => u.Activity == "Installing the database" && u.OverallPercent > 0));

        var mid = updates.Snapshot().Last(u => u.Activity == "Installing the database");
        Assert.Equal(SetupStepId.Build, mid.CurrentStep);
        Assert.InRange(mid.OverallPercent, 1, 99);
        release.SetResult();
        await run;
    }

    // ── power request ────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePowerRequest_IsTakenWhenTheFirstWorkingStepStarts_AndReleasedOnSuccess()
    {
        var power = new FakePower();
        var steps = AllSteps();
        steps[0].Done = true; // skipped: must not take it yet
        steps[1].Work = (_, _) => { Assert.Equal(1, power.Active); return Task.FromResult(SetupStepResult.Success()); };

        await MakeOp(steps, power).RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);

        Assert.Equal(1, power.Held);
        Assert.Equal(0, power.Active);
    }

    [Fact]
    public async Task ThePowerRequest_IsReleasedOnFailure_AndOnCancel()
    {
        var failPower = new FakePower();
        var failing = AllSteps();
        failing[0].Work = (_, _) => Task.FromResult(SetupStepResult.Failed(FailureCode.Unknown, "boom"));
        await MakeOp(failing, failPower).RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);
        Assert.Equal(0, failPower.Active);
        Assert.Equal(1, failPower.Held);

        var cancelPower = new FakePower();
        using var cts = new CancellationTokenSource();
        var cancelling = AllSteps();
        cancelling[0].Work = async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return SetupStepResult.Success(); };
        await MakeOp(cancelling, cancelPower).RunAsync(new SetupRequest(), new Collector(), cts.Token);
        Assert.Equal(0, cancelPower.Active);
        Assert.Equal(1, cancelPower.Held);
    }

    [Fact]
    public async Task AStepThatThrows_FailsTheRunWithTheUnknownCode_InsteadOfEndingTheApp()
    {
        var steps = AllSteps();
        steps[0].Work = (_, _) => throw new InvalidOperationException("kaboom");

        var result = await MakeOp(steps).RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.Unknown, result.Code);
        Assert.Contains("kaboom", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheChoiceToStartAtSignIn_IsSavedWithTheStartRequest()
    {
        var setup = NewSetupStore();

        await MakeOp(AllSteps(), setupStore: setup).RunAsync(new SetupRequest(StartAtSignIn: false), new Collector(), CancellationToken.None);

        Assert.False(setup.Read().StartAtSignIn);
    }

    [Fact]
    public async Task ASecondRunWhileOneIsWorking_IsRefusedByTheService_NotByTheOperation()
    {
        // The operation reports that it is working, so status can show it. The lease that makes a
        // second start return busy belongs to ApplianceService and is covered by its own tests.
        var steps = AllSteps();
        var release = new TaskCompletionSource();
        steps[0].Work = async (_, ct) => { await release.Task.WaitAsync(ct); return SetupStepResult.Success(); };
        var op = MakeOp(steps);

        Assert.False(op.IsWorking);
        var run = op.RunAsync(new SetupRequest(), new Collector(), CancellationToken.None);
        await WaitFor(() => op.IsWorking);

        release.SetResult();
        await run;
        Assert.False(op.IsWorking);
    }

    [Fact]
    public async Task APreflightFailure_HaltsBeforeRunningAnySteps_AndRecordsFailure()
    {
        var steps = AllSteps();
        steps[1].PreflightWork = _ => SetupStepResult.Failed(FailureCode.DataUnusable, "Data unusable at preflight");

        var setup = NewSetupStore();
        var updates = new Collector();
        var result = await MakeOp(steps, setupStore: setup).RunAsync(new SetupRequest(), updates, CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.DataUnusable, result.Code);
        Assert.Equal(SetupStepId.CompanyData, result.FailedStep);
        Assert.Equal(0, steps[0].Runs);
        Assert.Equal(0, steps[1].Runs);
        Assert.Equal(0, steps[2].Runs);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(condition(), "The condition did not become true within 10 seconds.");
    }
}
