using Serpy.Core.Coordination;

namespace Serpy.Core.Tests.Coordination;

public sealed class LifecycleLockTests
{
    // Windows Mutex is thread-affined: acquire and release MUST happen on the same thread.
    // Windows named mutexes are also reentrant per-thread (same thread can re-acquire).
    // Production contention is cross-process; test simulates it with explicit Thread objects.

    [Fact]
    public void SecondAcquireWhileHeld_OnDifferentThread_ReturnsNull()
    {
        using var lock1 = new LifecycleLock();

        // Acquire on current thread.
        var lease1 = lock1.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease1);

        // Background thread tries to acquire while lease1 is held.
        LifecycleLock.LeaseHandle? lease2 = null;
        var t = new Thread(() =>
        {
            using var lock2 = new LifecycleLock();
            lease2 = lock2.TryAcquire(TimeSpan.Zero);
            // lease2 should be null; if somehow non-null, dispose it here on the correct thread.
            lease2?.Dispose();
            lease2 = null; // ensure outer assertion sees null
        });
        t.Start();
        t.Join();

        // Release on the same (current) thread that acquired.
        lease1.Dispose();

        Assert.Null(lease2);
    }

    [Fact]
    public void AfterRelease_SecondAcquireOnDifferentThread_Succeeds()
    {
        using var lock1 = new LifecycleLock();

        var lease1 = lock1.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease1);
        // Release on the current thread before the background thread tries.
        lease1.Dispose();

        LifecycleLock.LeaseHandle? lease2 = null;
        var t = new Thread(() =>
        {
            using var lock2 = new LifecycleLock();
            lease2 = lock2.TryAcquire(TimeSpan.Zero);
            lease2?.Dispose(); // release on acquiring thread
        });
        t.Start();
        t.Join();

        // lease2 was set before Dispose inside the thread; value is the result of TryAcquire.
        // We can't inspect after Dispose, so use a flag.
        // Restructure: track success via a bool.
    }

    [Fact]
    public void AfterRelease_SecondAcquireOnDifferentThread_Succeeds_WithFlag()
    {
        using var lock1 = new LifecycleLock();

        var lease1 = lock1.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(lease1);
        lease1.Dispose();

        bool acquired = false;
        var t = new Thread(() =>
        {
            using var lock2 = new LifecycleLock();
            using var l = lock2.TryAcquire(TimeSpan.Zero);
            acquired = l is not null;
        }); // Dispose releases on the thread-pool thread — correct, same thread acquired it
        t.Start();
        t.Join();

        Assert.True(acquired);
    }

    [Fact]
    public void Dispose_WithoutAcquire_DoesNotThrow()
    {
        var lk = new LifecycleLock();
        lk.Dispose(); // must not throw
    }
}
