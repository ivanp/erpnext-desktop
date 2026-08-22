using System.Diagnostics;

namespace Serpy.App.Platform;

/// <summary>
/// Opens the default browser to the ERPNext loopback URL exactly once per start (AE8, R17).
/// Idempotent: a second call with the same URL within the same app lifetime is a no-op.
/// </summary>
public sealed class BrowserLauncher
{
    private string? _lastOpenedUrl;

    /// <summary>
    /// Open <paramref name="url"/> in the default browser.
    /// Returns true if the browser was launched; false if this URL was already opened this session.
    /// </summary>
    public bool OpenOnce(string url)
    {
        if (_lastOpenedUrl == url) return false;
        _lastOpenedUrl = url;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            // Non-fatal: the app continues without opening the browser.
            return false;
        }
    }

    /// <summary>Reset so the next call to OpenOnce fires the browser again.</summary>
    public void ResetForNewStart() => _lastOpenedUrl = null;
}
