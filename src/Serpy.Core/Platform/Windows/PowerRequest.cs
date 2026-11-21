using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serpy.Core.Setup;

namespace Serpy.Core.Platform.Windows;

/// <summary>
/// Keeps the PC awake via a process-owned Win32 power request (KTD21, R25).
/// Holds PowerRequestSystemRequired while a setup step works.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerRequest : IPowerRequest
{
    private readonly object _gate = new();
    private IntPtr _handle = IntPtr.Zero;
    private int _holdCount;

    public IDisposable Hold()
    {
        lock (_gate)
        {
            if (_holdCount == 0)
            {
                _handle = CreateRequest();
                if (_handle != IntPtr.Zero && _handle != (IntPtr)(-1))
                {
                    PowerSetRequest(_handle, PowerRequestSystemRequired);
                }
            }
            _holdCount++;
            return new PowerHold(this);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_holdCount > 0)
            {
                _holdCount--;
                if (_holdCount == 0 && _handle != IntPtr.Zero && _handle != (IntPtr)(-1))
                {
                    PowerClearRequest(_handle, PowerRequestSystemRequired);
                    CloseHandle(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }
    }

    private sealed class PowerHold(PowerRequest owner) : IDisposable
    {
        private PowerRequest? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }

    private const int PowerRequestSystemRequired = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x00000001;

    private static IntPtr CreateRequest()
    {
        var reasonPtr = Marshal.StringToHGlobalUni("Serpy setup in progress");
        try
        {
            var ctx = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = reasonPtr,
            };
            return PowerCreateRequest(in ctx);
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(reasonPtr);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(in REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
