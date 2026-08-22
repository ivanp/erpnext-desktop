using System.Text.RegularExpressions;

namespace Serpy.Core.Versions;

/// <summary>
/// Parses `bench version` output into exact Frappe and ERPNext versions.
/// Expected lines include, for example:
///   frappe 16.12.0
///   erpnext 16.8.1
/// Additional branch/hash text after the version is ignored.
/// Missing required applications is a hard build failure.
/// </summary>
public static partial class BenchVersionParser
{
    [GeneratedRegex(@"^(?<app>frappe|erpnext)\s+(?:v)?(?<version>\d+(?:\.\d+)+(?:[-+~][^\s]+)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AppVersionRegex();

    public static (string Frappe, string ErpNext) ParseRequired(string output)
    {
        string? frappe = null;
        string? erpnext = null;

        foreach (Match match in AppVersionRegex().Matches(output))
        {
            var app = match.Groups["app"].Value;
            var version = match.Groups["version"].Value;
            if (app.Equals("frappe", StringComparison.OrdinalIgnoreCase))
                frappe = version;
            else if (app.Equals("erpnext", StringComparison.OrdinalIgnoreCase))
                erpnext = version;
        }

        if (string.IsNullOrWhiteSpace(frappe) || string.IsNullOrWhiteSpace(erpnext))
            throw new InvalidDataException(
                "bench version did not report required app versions. " +
                $"frappe={(frappe ?? "missing")} erpnext={(erpnext ?? "missing")}. " +
                $"Output: {output.Trim()}");

        return (frappe, erpnext);
    }
}
