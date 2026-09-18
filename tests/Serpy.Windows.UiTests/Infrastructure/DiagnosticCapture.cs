using System.Text;
using FlaUI.Core.Capturing;

namespace Serpy.Windows.UiTests.Infrastructure;

public static class DiagnosticCapture
{
    private static readonly string ArtifactsBaseDir =
        Path.Combine(AppContext.BaseDirectory, "artifacts", "ui-test-diagnostics");

    public static string Capture(Application? app, Window? window, string testName, string? sandboxAppDataDir)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var outputDir = Path.Combine(ArtifactsBaseDir, $"{testName}_{timestamp}");
        Directory.CreateDirectory(outputDir);

        // 1. Screenshot
        try
        {
            var screenshotPath = Path.Combine(outputDir, "desktop.png");
            using var img = FlaUI.Core.Capturing.Capture.Screen();
            img.ToFile(screenshotPath);

            if (window is not null)
            {
                var winPath = Path.Combine(outputDir, "window.png");
                using var winImg = FlaUI.Core.Capturing.Capture.Element(window);
                winImg.ToFile(winPath);
            }
        }
        catch { /* best effort */ }

        // 2. UI Tree Dump
        try
        {
            var treePath = Path.Combine(outputDir, "uitree.txt");
            var sb = new StringBuilder();
            if (window is not null)
            {
                var rootId = window.Properties.AutomationId.ValueOrDefault ?? "none";
                sb.AppendLine($"--- Root Window: '{window.Title}' AutomationId='{rootId}' ---");
                DumpElement(window, sb, 0);

                sb.AppendLine("\n--- FindAllDescendants: ---");
                try
                {
                    var descendants = window.FindAllDescendants();
                    sb.AppendLine($"Descendants count: {descendants.Length}");
                    foreach (var d in descendants)
                    {
                        var dId = d.Properties.AutomationId.ValueOrDefault;
                        var dName = d.Properties.Name.ValueOrDefault;
                        var dClass = d.Properties.ClassName.ValueOrDefault;
                        var dType = d.Properties.ControlType.ValueOrDefault.ToString();
                        sb.AppendLine($"  - [{dType}] ID='{dId}' Name='{dName}' Class='{dClass}'");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Error finding descendants: {ex.Message}");
                }
            }
            File.WriteAllText(treePath, sb.ToString());
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(outputDir, "capture_error.txt"), ex.ToString()); } catch { }
        }

        // 3. Application Logs
        if (!string.IsNullOrEmpty(sandboxAppDataDir))
        {
            try
            {
                var logsDir = Path.Combine(sandboxAppDataDir, "logs");
                if (Directory.Exists(logsDir))
                {
                    var destLogs = Path.Combine(outputDir, "logs");
                    Directory.CreateDirectory(destLogs);
                    foreach (var file in Directory.GetFiles(logsDir))
                    {
                        File.Copy(file, Path.Combine(destLogs, Path.GetFileName(file)), overwrite: true);
                    }
                }
            }
            catch { /* best effort */ }
        }

        return outputDir;
    }

    private static void DumpElement(AutomationElement element, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);
        try
        {
            var id = element.Properties.AutomationId.ValueOrDefault;
            var name = element.Properties.Name.ValueOrDefault;
            var type = element.Properties.ControlType.ValueOrDefault.ToString();
            var enabled = element.Properties.IsEnabled.ValueOrDefault ? "Enabled" : "Disabled";

            sb.AppendLine($"{indent}[{type}] ID='{id}' Name='{name}' ({enabled})");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{indent}[ERROR getting properties]: {ex.Message}");
        }

        try
        {
            var children = element.FindAllChildren();
            foreach (var child in children)
            {
                DumpElement(child, sb, depth + 1);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{indent}[ERROR getting children]: {ex.Message}");
        }
    }
}
