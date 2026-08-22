using System.Text.RegularExpressions;

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
    // FQDN only: safe as a process argument and valid for Frappe's site directory.
    private static readonly Regex SiteNamePattern = new(
        @"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.CultureInvariant);

    public static InitializationParameters Default => new("site1.local", string.Empty);

    public static bool IsValidSiteName(string? siteName) =>
        siteName is not null && SiteNamePattern.IsMatch(siteName);
}
