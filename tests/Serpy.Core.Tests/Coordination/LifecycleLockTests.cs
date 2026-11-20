using System.Diagnostics;
using Serpy.Core.Configuration;
using Serpy.Core.Coordination;

namespace Serpy.Core.Tests.Coordination;

// Sets the process-wide app-data override, so it must not run beside other tests that do.
[Collection("LifecycleLock")]
public sealed class LifecycleLockTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previous = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly List<string> _dirs = [];

    public LifecycleLockTests() => UseNewAppDataRoot();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previous);
        foreach (var dir in _dirs)
            if (Directory.Exists(dir)) try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private string UseNewAppDataRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"SerpyLock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        Environment.SetEnvironmentVariable(OverrideVariable, dir);
        return dir;
    }

    [Fact]
    public async Task ALeaseTakenOnOneThread_IsReleasedOnAnotherWithoutAnError()
    {
        using var lk = new LifecycleLock();
        var lease = lk.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease);

        // An operation that awaits resumes on a pool thread and disposes its lease there.
        await Task.Run(() => lease.Dispose());

        using var again = lk.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task AnOperationThatMovesThreadsMidway_StillFreesTheLease()
    {
        using var lk = new LifecycleLock();

        await Task.Run(async () =>
        {
            using var lease = lk.TryAcquire(TimeSpan.Zero);
            Assert.NotNull(lease);
            await Task.Delay(20);
            await Task.Yield();
        });

        using var next = lk.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(next);
    }

    [Fact]
    public void ASecondAcquireWhileTheLeaseIsHeld_ReturnsNothingAfterItsTimeout()
    {
        using var first = new LifecycleLock();
        using var second = new LifecycleLock();
        using var lease = first.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease);

        var clock = Stopwatch.StartNew();
        var other = second.TryAcquire(TimeSpan.FromMilliseconds(200));
        clock.Stop();

        Assert.Null(other);
        Assert.InRange(clock.ElapsedMilliseconds, 150, 5_000);
    }

    [Fact]
    public void AnAcquireWithTime_SucceedsWhenTheHolderReleasesInside_ItsTimeout()
    {
        using var first = new LifecycleLock();
        using var second = new LifecycleLock();
        var lease = first.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease);
        _ = Task.Run(async () => { await Task.Delay(100); lease.Dispose(); });

        using var other = second.TryAcquire(TimeSpan.FromSeconds(10));

        Assert.NotNull(other);
    }

    [Fact]
    public void AnAcquireSucceeds_AfterAHolderThatNeverReleasedLostItsHandle()
    {
        // A crashed process never calls Release; the OS closes its handle. Holding the file open
        // with no sharing and then closing the handle leaves the same trace.
        var lockFile = Path.Combine(KnownPaths.SettingsDir, "lifecycle.lock");
        Directory.CreateDirectory(KnownPaths.SettingsDir);
        var crashedHolder = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        using var lk = new LifecycleLock();
        Assert.Null(lk.TryAcquire(TimeSpan.Zero));

        crashedHolder.Dispose();

        using var lease = lk.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease);
    }

    [Fact]
    public void TwoAppDataRoots_DoNotContend()
    {
        using var inFirstRoot = new LifecycleLock();
        using var leaseOne = inFirstRoot.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(leaseOne);

        UseNewAppDataRoot();
        using var inSecondRoot = new LifecycleLock();
        using var leaseTwo = inSecondRoot.TryAcquire(TimeSpan.Zero);

        Assert.NotNull(leaseTwo);
    }

    [Fact]
    public void ALeaseDisposedTwice_NeverReleasesTheNextLease()
    {
        using var lk = new LifecycleLock();
        var stale = lk.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(stale);
        stale.Dispose();

        using var current = lk.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(current);
        stale.Dispose();

        using var other = new LifecycleLock();
        Assert.Null(other.TryAcquire(TimeSpan.Zero));
    }
}
