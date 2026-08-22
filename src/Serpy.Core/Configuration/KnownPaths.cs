namespace Serpy.Core.Configuration;

/// <summary>
/// Platform-appropriate base directories for Serpy data.
/// User data never moves when the product bundle updates.
/// </summary>
public static class KnownPaths
{
    private const string AppName = "Serpy";

    /// <summary>
    /// Per-user application data root.
    /// Windows: %LOCALAPPDATA%\Serpy
    /// macOS:   ~/Library/Application Support/Serpy
    /// Linux:   $XDG_DATA_HOME/Serpy  (fallback: ~/.local/share/Serpy)
    /// </summary>
    public static string AppDataRoot { get; } = ResolveAppDataRoot();

    /// <summary>Appliance workspace: system.qcow2, data.img, recovery journal.</summary>
    public static string ApplianceDir => Path.Combine(AppDataRoot, "appliance");

    /// <summary>Downloaded and extracted managed QEMU bundle.</summary>
    public static string RuntimeDir => Path.Combine(AppDataRoot, "runtime");

    /// <summary>Operation logs.</summary>
    public static string LogsDir => Path.Combine(AppDataRoot, "logs");

    /// <summary>Persisted settings and state JSON files.</summary>
    public static string SettingsDir => Path.Combine(AppDataRoot, "settings");

    private static string ResolveAppDataRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create);
            return Path.Combine(localAppData, AppName);
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppName);
        }

        // Linux / XDG
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdgData))
            return Path.Combine(xdgData, AppName);

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".local", "share", AppName);
    }
}
