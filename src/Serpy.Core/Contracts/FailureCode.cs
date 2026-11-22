namespace Serpy.Core.Contracts;

/// <summary>
/// Typed reason for a failed operation (KTD9). The app maps each code to plain words;
/// the raw message stays in the details panel only.
/// </summary>
public enum FailureCode
{
    None = 0,
    Unknown,
    Cancelled,
    PcCheckFinding,
    ElevationDeclined,
    RestartRequired,
    NetworkUnavailable,
    DiskFull,
    ChecksumMismatch,
    ProvisioningNotStarted,
    ProvisioningFailed,
    GuestConsoleClosed,
    PasswordRejected,
    DataUnusable,
    PortInUse,
}
