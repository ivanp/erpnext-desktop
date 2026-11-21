namespace Serpy.Core.Setup;

/// <summary>
/// Keeps the PC awake while setup works (KTD21). It is a seam, so that tests use a fake and the
/// Windows implementation arrives with the rest of the Windows platform code.
/// </summary>
public interface IPowerRequest
{
    /// <summary>Takes the request. Disposing the result releases it, and may be repeated.</summary>
    IDisposable Hold();
}

/// <summary>The request on platforms and in tests that have none.</summary>
public sealed class NoPowerRequest : IPowerRequest
{
    public static readonly NoPowerRequest Instance = new();

    public IDisposable Hold() => EmptyHold.Instance;

    private sealed class EmptyHold : IDisposable
    {
        public static readonly EmptyHold Instance = new();
        public void Dispose() { }
    }
}
