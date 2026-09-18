namespace Serpy.Windows.UiTests.Infrastructure;

public sealed class UiTestFixture : IDisposable
{
    private readonly string _sandboxDir;
    private readonly UIA3Automation _automation;
    private Application? _app;

    public string SandboxAppDataDir => _sandboxDir;
    public UIA3Automation Automation => _automation;
    public Application? App => _app;

    public UiTestFixture()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"SerpyUiTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxDir);
        Directory.CreateDirectory(Path.Combine(_sandboxDir, "appliance"));
        Directory.CreateDirectory(Path.Combine(_sandboxDir, "settings"));
        Directory.CreateDirectory(Path.Combine(_sandboxDir, "logs"));

        _automation = new UIA3Automation();
    }

    public static string ResolveAppExecutable()
    {
        var dir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(dir, "..", "..", "..", "..", "..", "src", "Serpy.App", "bin", "Release", "net10.0", "win-x64", "publish", "Serpy.App.exe"),
            Path.Combine(dir, "..", "..", "..", "..", "..", "src", "Serpy.App", "bin", "Release", "net10.0", "win-x64", "Serpy.App.exe"),
            Path.Combine(dir, "..", "..", "..", "..", "..", "src", "Serpy.App", "bin", "Release", "net10.0", "Serpy.App.exe"),
            Path.Combine(dir, "Serpy.App.exe"),
        ];

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) return full;
        }

        throw new FileNotFoundException(
            "Could not locate Serpy.App.exe. Build or publish Serpy.App before running UI tests.");
    }

    public Application LaunchApp(string? arguments = null)
    {
        var exePath = ResolveAppExecutable();
        // Ensure no stale instance is holding the mutex
        foreach (var p in Process.GetProcessesByName("Serpy.App"))
        {
            try { p.Kill(); p.WaitForExit(1000); } catch { /* ignore */ }
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["SERPY_TEST_APPDATA"] = _sandboxDir;

        _app = Application.Launch(psi);
        return _app;
    }

    public Window GetMainWindow(TimeSpan? timeout = null)
    {
        if (_app is null)
            throw new InvalidOperationException("App has not been launched.");

        var waitTime = timeout ?? TimeSpan.FromSeconds(10);
        var window = _app.GetMainWindow(_automation, waitTime);
        if (window is null)
        {
            throw new TimeoutException($"Timed out waiting {waitTime.TotalSeconds}s for Serpy main window.");
        }
        return window;
    }

    public void SeedApplianceFile(string relativePath, string content)
    {
        var target = Path.Combine(_sandboxDir, "appliance", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }

    public void CopyRuntimeBundleFromDefaultIfAvailable()
    {
        try
        {
            var defaultRuntime = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Serpy", "runtime");
            if (Directory.Exists(defaultRuntime))
            {
                var sandboxRuntime = Path.Combine(_sandboxDir, "runtime");
                Directory.CreateDirectory(sandboxRuntime);
                // Shallow copy or symlink if needed
            }
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        try
        {
            if (_app is not null && !_app.HasExited)
            {
                try { _app.Close(); } catch { /* ignore */ }
                if (!_app.HasExited)
                {
                    try { _app.Kill(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }

        try
        {
            _automation.Dispose();
        }
        catch { /* ignore */ }

        try
        {
            if (Directory.Exists(_sandboxDir))
            {
                Directory.Delete(_sandboxDir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }
}
