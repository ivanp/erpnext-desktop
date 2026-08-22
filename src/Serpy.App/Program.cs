using Avalonia;
using Serpy.App;

// Single-instance guard: if another Serpy.App is already running for this user,
// bring its window to focus and exit. The mutex is intentionally not released
// until process exit so the OS cleans it up on crash too.
string singleInstanceMutexName = $"Local\\Serpy-App-{Environment.UserName}";
using var singleInstanceMutex = new System.Threading.Mutex(true, singleInstanceMutexName, out bool isFirstInstance);
if (!isFirstInstance)
{
    // A second launch: signal the existing instance to show its dashboard.
    // For now just exit; U6 adds the named-pipe activation.
    return 0;
}

bool trayOnly = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

return BuildAvaloniaApp(trayOnly)
    .StartWithClassicDesktopLifetime(args);

static AppBuilder BuildAvaloniaApp(bool trayOnly) =>
    AppBuilder.Configure(() => new App(trayOnly))
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
