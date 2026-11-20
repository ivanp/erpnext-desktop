using Serpy.Core.Configuration;

namespace Serpy.Core.Coordination;

/// <summary>
/// Per-user file lease that serializes mutating lifecycle operations (KTD5). The lease is an
/// exclusive open of <c>lifecycle.lock</c> under the settings folder: the OS frees it when the
/// holder's handle closes, so a crashed process cannot leave it held, and it can be released from
/// any thread, which an operation that awaits needs. The settings folder follows the app-data
/// root, so a different root is a different lock and tests stay isolated.
/// Status reads MUST NOT acquire this lock.
/// </summary>
public sealed class LifecycleLock : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);

    private readonly string _lockFile = Path.Combine(KnownPaths.SettingsDir, "lifecycle.lock");
    private FileStream? _held;

    /// <summary>
    /// Try to acquire the lifecycle lease, retrying until <paramref name="timeout"/> has passed.
    /// Returns a disposable lease handle on success, or null if another holder still has it.
    /// </summary>
    public LeaseHandle? TryAcquire(TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockFile)!);
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var stream = TryOpenExclusive();
            if (stream is not null)
            {
                _held = stream;
                return new LeaseHandle(Release);
            }

            var remaining = timeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero) return null;
            Thread.Sleep(remaining < RetryInterval ? remaining : RetryInterval);
        }
    }

    // A sharing violation is an IOException on every platform: the lease is held by someone else.
    private FileStream? TryOpenExclusive()
    {
        try
        {
            return new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void Release() => Interlocked.Exchange(ref _held, null)?.Dispose();

    public void Dispose() => Release();

    /// <summary>Releases the lease on the first Dispose, from any thread; later calls do nothing.</summary>
    public sealed class LeaseHandle(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
        }
    }
}
