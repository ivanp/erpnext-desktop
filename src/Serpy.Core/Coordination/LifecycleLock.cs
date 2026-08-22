namespace Serpy.Core.Coordination;

/// <summary>
/// Per-user named mutex that serializes mutating lifecycle operations.
/// Status reads MUST NOT acquire this lock.
/// The mutex is held only for the duration of a mutation (validate → write state → execute).
/// </summary>
public sealed class LifecycleLock : IDisposable
{
    // Per-user scope: the SID is baked into the name so two Windows user sessions
    // each get their own mutex and never block each other.
    private static readonly string s_mutexName =
        $@"Local\Serpy-lifecycle-{Environment.UserName}";

    private readonly Mutex _mutex = new(false, s_mutexName);
    private bool _held;

    /// <summary>
    /// Try to acquire the lifecycle lease.
    /// Returns a disposable lease handle on success, or null if already held.
    /// </summary>
    public LeaseHandle? TryAcquire(TimeSpan timeout)
    {
        if (_mutex.WaitOne(timeout))
        {
            _held = true;
            return new LeaseHandle(Release);
        }
        return null;
    }

    private void Release()
    {
        if (_held)
        {
            _held = false;
            _mutex.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        Release();
        _mutex.Dispose();
    }

    public sealed class LeaseHandle(Action release) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (!_disposed) { _disposed = true; release(); }
        }
    }
}
