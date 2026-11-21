using System.Text;
using System.Text.Json;
using Serpy.Core.Configuration;
using Serpy.Core.Coordination;
using Serpy.Core.Versions;

namespace Serpy.Core.Setup;

/// <summary>
/// Assembles the one text file that Save details writes (KTD22): the PC check report, both state
/// files, the pinned versions and the operation logs of the current setup run. It reads only what
/// is passed in. The saved credential, the certificate store and the private keys are never inputs,
/// so none of them can reach the file, and the logs are already masked where they are written.
/// </summary>
public static class DetailsReport
{
    // A full build log runs to tens of thousands of lines. The end of it holds the failure.
    private const int MaxLinesPerLog = 4000;

    public static string Compose(
        string? pcCheckReport,
        SetupState setupState,
        ApplianceState applianceState,
        VersionManifest? manifest,
        IEnumerable<string> logPaths,
        DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.AppendLine("Serpy setup details");
        text.AppendLine($"Created {now:yyyy-MM-dd HH:mm:ss zzz}");

        Section(text, "PC check");
        text.AppendLine(string.IsNullOrWhiteSpace(pcCheckReport) ? "No PC check ran in this session." : pcCheckReport.TrimEnd());

        Section(text, "Setup state");
        text.AppendLine(JsonSerializer.Serialize(setupState, SetupStateJsonContext.Default.SetupState));

        Section(text, "Appliance state");
        text.AppendLine(JsonSerializer.Serialize(applianceState, ApplianceStateJsonContext.Default.ApplianceState));

        Section(text, "Pinned versions");
        if (manifest is null)
        {
            text.AppendLine("Not available.");
        }
        else
        {
            text.AppendLine($"QEMU {manifest.Qemu.Version}");
            text.AppendLine($"Debian cloud image {manifest.DebianCloudImage.Version}");
            text.AppendLine($"Python {manifest.Runtime.Python.Lock}, Node {manifest.Runtime.Node.Lock}, " +
                            $"MariaDB {manifest.Runtime.MariaDb.Lock}, Redis {manifest.Runtime.Redis.Lock}");
            text.AppendLine($"Frappe {manifest.Apps.Frappe.Lock}, ERPNext {manifest.Apps.ErpNext.Lock}");
        }

        foreach (var path in logPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Section(text, $"Log {Path.GetFileName(path)}");
            AppendLogTail(text, path);
        }

        return text.ToString();
    }

    private static void Section(StringBuilder text, string title)
    {
        text.AppendLine();
        text.AppendLine($"=== {title} ===");
    }

    private static void AppendLogTail(StringBuilder text, string path)
    {
        try
        {
            // The log may still be open for writing, so share the file with its writer.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var tail = new Queue<string>();
            var dropped = 0;
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > MaxLinesPerLog)
                {
                    tail.Dequeue();
                    dropped++;
                }
            }

            if (dropped > 0) text.AppendLine($"({dropped} earlier lines left out)");
            foreach (var line in tail) text.AppendLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text.AppendLine($"The log could not be read: {ex.Message}");
        }
    }
}
