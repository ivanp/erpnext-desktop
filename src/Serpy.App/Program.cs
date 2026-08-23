using Avalonia;
using Serpy.App;
using Serpy.Core.Configuration;
using Serpy.Core.Coordination;
using Serpy.Core.Health;
using Serpy.Core.Images;
using Serpy.Core.Operations;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;

// Single-instance guard: if another Serpy.App is already running for this user,
// bring its window to focus and exit.
string singleInstanceMutexName = $"Local\\Serpy-App-{Environment.UserName}";
using var singleInstanceMutex = new System.Threading.Mutex(true, singleInstanceMutexName, out bool isFirstInstance);
if (!isFirstInstance)
    return 0;

bool trayOnly = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

// ── Composition root: construct the real ApplianceService ─────────────────
var settings      = new ApplianceSettings();
var manifest      = VersionManifestLoader.Load();
var runtimeManifest = new RuntimeManifest
{
    QemuVersion = manifest.Qemu.Version,
    Windows     = new RuntimeManifest.WindowsBundle
    {
        ArchiveUrl       = manifest.Qemu.Windows.ArchiveUrl,
        ArchiveSha256    = manifest.Qemu.Windows.ArchiveSha256,
        SourceUrl        = manifest.Qemu.Windows.SourceUrl,
        LicenseNoticeUrl = manifest.Qemu.Windows.LicenseNoticeUrl,
    },
};
var resolver      = new ManagedRuntimeResolver(runtimeManifest);
var certStore     = new TlsCertificateStore();
var imageTool     = new QemuImageTool(resolver.QemuImgExe);
var imageDl       = new BaseImageDownloader(manifest);
var cloudInitDir  = FindCloudInitDir();
var seedWriter    = new NoCloudSeedWriter(
    NoCloudSeedWriter.RenderUserData(
        File.ReadAllText(Path.Combine(cloudInitDir, "user-data")),
        File.ReadAllText(FindGuestHelper("provision-done.sh")),
        File.ReadAllText(FindGuestHelper("init-data.sh")),
        File.ReadAllText(FindGuestHelper("recover.sh"))),
    File.ReadAllText(Path.Combine(cloudInitDir, "meta-data")));
var healthCreds   = new HealthCredentials();
var stateStore    = new StateStore();

var service = new ApplianceService(
    resolver, certStore, imageTool, imageDl, seedWriter,
    healthCreds, stateStore, manifest, settings);

return BuildAvaloniaApp(trayOnly, service)
    .StartWithClassicDesktopLifetime(args);

// ── Helpers ───────────────────────────────────────────────────────────────

static AppBuilder BuildAvaloniaApp(bool trayOnly, Serpy.Core.Contracts.IApplianceService service) =>
    AppBuilder.Configure(() =>
    {
        var app = new App(trayOnly) { Service = service };
        return app;
    })
    .UsePlatformDetect()
    .WithInterFont()
    .LogToTrace();

static string FindGuestHelper(string name)
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8; i++)
    {
        var candidate = Path.Combine(dir, "guest", name);
        if (File.Exists(candidate)) return candidate;
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent is null || parent == dir) break;
        dir = parent;
    }
    return Path.Combine(AppContext.BaseDirectory, "guest", name);
}

static string FindCloudInitDir()
{
    // Walk up from the executable directory to find build/cloud-init/.
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8; i++)
    {
        var candidate = Path.Combine(dir, "build", "cloud-init");
        if (Directory.Exists(candidate)) return candidate;
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent is null || parent == dir) break;
        dir = parent;
    }
    // Fallback: copy cloud-init to the app output directory during packaging.
    return Path.Combine(AppContext.BaseDirectory, "cloud-init");
}