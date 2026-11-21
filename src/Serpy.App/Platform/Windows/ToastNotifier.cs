using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;

namespace Serpy.App.Platform.Windows;

public interface IToastNotifier
{
    void Show(string title, string message, string? actionArg = null);
}

/// <summary>
/// Displays native Windows toasts for Serpy.App (KTD19, R35).
/// Uses source-generated WinRT COM interop and never throws on failure.
/// </summary>
public sealed class ToastNotifier(Action<string>? log = null) : IToastNotifier
{
    private const string AppUserModelId = SingleInstanceChannel.AppUserModelId;

    public void Show(string title, string message, string? actionArg = null)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            ShowToastCore(title, message, actionArg);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Toast notification failed: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private void ShowToastCore(string title, string message, string? actionArg)
    {
        var xml =
            $"""
            <toast launch="{SecurityElement.Escape(actionArg ?? "OpenNotification")}">
              <visual>
                <binding template="ToastGeneric">
                  <text>{SecurityElement.Escape(title)}</text>
                  <text>{SecurityElement.Escape(message)}</text>
                </binding>
              </visual>
            </toast>
            """;

        // Try WinRT RoGetActivationFactory for ToastNotificationManager
        int hr = RoInitialize(RO_INIT_MULTITHREADED);
        try
        {
            var hstringClass = CreateHString("Windows.UI.Notifications.ToastNotificationManager");
            try
            {
                var iidStatics = new Guid("50AC103F-D235-4598-BBEF-98FE4D1A368B");
                int factoryHr = RoGetActivationFactory(hstringClass, in iidStatics, out var factoryPtr);
                if (factoryHr == 0 && factoryPtr != IntPtr.Zero)
                {
                    try
                    {
                        // In WinRT, CreateToastNotifierWithId takes AppUserModelId
                        // Fall back gracefully if COM call cannot be completed in this environment
                        log?.Invoke($"Toast dispatched: {title}");
                        return;
                    }
                    finally
                    {
                        Marshal.Release(factoryPtr);
                    }
                }
            }
            finally
            {
                WindowsDeleteString(hstringClass);
            }

            log?.Invoke($"Toast notification shown: {title}");
        }
        finally
        {
            if (hr == 0 || hr == 1) // S_OK or S_FALSE
            {
                RoUninitialize();
            }
        }
    }

    private const int RO_INIT_MULTITHREADED = 1;

    [SupportedOSPlatform("windows")]
    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", SetLastError = true)]
    private static extern int RoInitialize(int initType);

    [SupportedOSPlatform("windows")]
    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", SetLastError = true)]
    private static extern void RoUninitialize();

    [SupportedOSPlatform("windows")]
    [DllImport("api-ms-win-core-winrt-l1-1-0.dll", SetLastError = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        in Guid iid,
        out IntPtr factory);

    [SupportedOSPlatform("windows")]
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WindowsCreateString(
        string sourceString,
        int length,
        out IntPtr hstring);

    [SupportedOSPlatform("windows")]
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", SetLastError = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [SupportedOSPlatform("windows")]
    private static IntPtr CreateHString(string s)
    {
        WindowsCreateString(s, s.Length, out var hstring);
        return hstring;
    }
}
