namespace Serpy.Core.Contracts;

/// <summary>
/// Typed initialization input supplied by the UI credential dialog.
/// The admin password is in-memory only; it is stored via HealthCredentials
/// (DPAPI-encrypted) at the start of InitializeOperation and never returned,
/// logged, or placed in OperationUpdate messages or ApplianceState.
/// </summary>
public sealed record InitializationParameters(
    string SiteName,
    string AdminPassword)
{
    public static InitializationParameters Default =>
        new("site1.local", string.Empty);
}
