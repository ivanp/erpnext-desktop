using System.Text.RegularExpressions;

namespace Serpy.Core.Versions;

/// <summary>
/// Compares version strings for the R9 gate.
/// Supports:
///   - Simple SemVer-like tuples: "3.14.1"
///   - Debian epoch+suffix: "11.8.3-MariaDB-1:11.8.3+maria~deb13"
///   - Prerelease suffixes: "3.14.0a1", "3.14.0rc2"
///   - Multi-digit components: "24.2.0"
/// The "installed" version is always the raw string from QGA.
/// </summary>
public static class VersionEvaluator
{
    /// <summary>
    /// Compare two version strings. Returns negative if a &lt; b, zero if equal, positive if a &gt; b.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        return pa.CompareTo(pb);
    }

    /// <summary>
    /// True if <paramref name="installed"/> exactly matches <paramref name="locked"/>.
    /// Normalizes Debian suffixes before comparing.
    /// </summary>
    public static bool MatchesLock(string installed, string locked) =>
        Compare(installed, locked) == 0;

    /// <summary>
    /// True if <paramref name="installed"/> is >= <paramref name="floor"/>.
    /// </summary>
    public static bool MeetsFloor(string installed, string floor) =>
        Compare(installed, floor) >= 0;

    // ── Internal parsing ──────────────────────────────────────────────────

    private static ParsedVersion Parse(string raw)
    {
        // Strip Debian epoch "N:" prefix.
        var s = raw;
        var colonIdx = s.IndexOf(':');
        if (colonIdx >= 0 && colonIdx < 4) // e.g. "1:11.8.3..."
            s = s[(colonIdx + 1)..];

        // Strip Debian downstream suffix after first "+".
        var plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
            s = s[..plusIdx];

        // Strip Debian package suffix after "~" (e.g. "~deb13").
        var tildeIdx = s.IndexOf('~');
        if (tildeIdx >= 0)
            s = s[..tildeIdx];

        // Strip the "-MariaDB-..." or "-N" suffix (after "-").
        var dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
            s = s[..dashIdx];

        // Parse numeric components, then prerelease.
        // Example: "3.14.0rc2" → (3,14,0) with prerelease "rc2"
        var match = Regex.Match(s, @"^(\d+(?:\.\d+)*)(.*)$");
        if (!match.Success)
            return new ParsedVersion([0], string.Empty);

        var numericPart = match.Groups[1].Value;
        var prerelease = match.Groups[2].Value.Trim();

        var components = numericPart.Split('.').Select(int.Parse).ToArray();
        return new ParsedVersion(components, prerelease);
    }

    private sealed record ParsedVersion(int[] Components, string Prerelease) : IComparable<ParsedVersion>
    {
        public int CompareTo(ParsedVersion? other)
        {
            if (other is null) return 1;

            var len = Math.Max(Components.Length, other.Components.Length);
            for (int i = 0; i < len; i++)
            {
                var a = i < Components.Length ? Components[i] : 0;
                var b = i < other.Components.Length ? other.Components[i] : 0;
                if (a != b) return a.CompareTo(b);
            }

            // No prerelease > has prerelease (e.g. 3.14.1 > 3.14.1rc2).
            if (string.IsNullOrEmpty(Prerelease) && !string.IsNullOrEmpty(other.Prerelease)) return 1;
            if (!string.IsNullOrEmpty(Prerelease) && string.IsNullOrEmpty(other.Prerelease)) return -1;
            return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);
        }
    }
}
