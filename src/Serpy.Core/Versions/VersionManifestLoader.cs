namespace Serpy.Core.Versions;

/// <summary>
/// Parses config/versions.yaml into a typed VersionManifest.
/// Uses a minimal hand-written parser instead of YamlDotNet to maintain AOT compatibility.
/// The versions.yaml format is a fixed two-to-three-level nested key: value structure.
/// </summary>
public static class VersionManifestLoader
{
    public static VersionManifest Load()
    {
        var path = ResolveYamlPath()
            ?? throw new FileNotFoundException(
                "config/versions.yaml not found alongside the executable or in a parent directory.");
        return LoadFrom(path);
    }

    public static VersionManifest LoadFrom(string path)
    {
        var lines = File.ReadAllLines(path);
        // Build a flat key map: "qemu.windows.archiveUrl" → "value"
        var flat = ParseFlat(lines);
        return Hydrate(flat);
    }

    // ── Flat key-value parser ─────────────────────────────────────────────

    /// <summary>
    /// Convert indented YAML key: value lines into a flat dot-separated map.
    /// Handles up to 3 indent levels. Ignores comments and blank lines.
    /// Does NOT handle arrays, multi-line values, or anchors.
    /// </summary>
    private static Dictionary<string, string> ParseFlat(string[] lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<(int Indent, string Key)>(); // ancestor key path

        foreach (var rawLine in lines)
        {
            var line = rawLine;

            // Strip inline comments.
            var commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
                line = line[..commentIdx];

            if (string.IsNullOrWhiteSpace(line)) continue;

            // Measure indent (2-space convention).
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ')
                indent++;

            var trimmed = line.TrimStart();
            if (!trimmed.Contains(':')) continue;

            var colonIdx = trimmed.IndexOf(':');
            var key = trimmed[..colonIdx].Trim().ToLowerInvariant();
            var value = trimmed[(colonIdx + 1)..].Trim().Trim('"');

            // Pop stack entries at same or deeper indent.
            while (stack.Count > 0 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);

            var fullKey = stack.Count > 0
                ? string.Join('.', stack.Select(s => s.Key)) + '.' + key
                : key;

            if (!string.IsNullOrEmpty(value))
                result[fullKey] = value;
            else
                stack.Add((indent, key)); // nested section
        }

        return result;
    }

    private static VersionManifest Hydrate(Dictionary<string, string> m)
    {
        string Get(string key) => m.TryGetValue(key, out var v) ? v : string.Empty;

        return new VersionManifest
        {
            Qemu = new VersionManifest.QemuSection
            {
                Version = Get("qemu.version"),
                Windows = new VersionManifest.QemuSection.WindowsBundle
                {
                    ArchiveUrl       = Get("qemu.windows.archiveurl"),
                    ArchiveSha256    = Get("qemu.windows.archivesha256"),
                    InstallerUrl     = Get("qemu.windows.installerurl"),
                    InstallerSha256  = Get("qemu.windows.installersha256"),
                    SourceUrl        = Get("qemu.windows.sourceurl"),
                    LicenseNoticeUrl = Get("qemu.windows.licensenoticeurl"),
                },
            },
            DebianCloudImage = new VersionManifest.DebianCloudImageSection
            {
                Version = Get("debiancloudimage.version"),
                Url = Get("debiancloudimage.url"),
                Sha512 = Get("debiancloudimage.sha512"),
                Release = Get("debiancloudimage.release"),
                Arch = Get("debiancloudimage.arch"),
            },
            Runtime = new VersionManifest.RuntimeSection
            {
                Python = new VersionManifest.LockFloor
                    { Lock = Get("runtime.python.lock"), Floor = Get("runtime.python.floor") },
                Node = new VersionManifest.LockFloor
                    { Lock = Get("runtime.node.lock"), Floor = Get("runtime.node.floor") },
                MariaDb = new VersionManifest.LockFloor
                    { Lock = Get("runtime.mariadb.lock"), Floor = Get("runtime.mariadb.floor") },
                Redis = new VersionManifest.LockFloor
                    { Lock = Get("runtime.redis.lock"), Floor = Get("runtime.redis.floor") },
            },
            Apps = new VersionManifest.AppsSection
            {
                Frappe = new VersionManifest.AppEntry
                    { Branch = Get("apps.frappe.branch"), Commit = Get("apps.frappe.commit"), Lock = Get("apps.frappe.lock"), MinVersion = Get("apps.frappe.minversion") },
                ErpNext = new VersionManifest.AppEntry
                    { Branch = Get("apps.erpnext.branch"), Commit = Get("apps.erpnext.commit"), Lock = Get("apps.erpnext.lock"), MinVersion = Get("apps.erpnext.minversion") },
            },
        };
    }

    private static string? ResolveYamlPath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "config", "versions.yaml");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }
}
