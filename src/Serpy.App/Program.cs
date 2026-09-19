using Avalonia;
using Serpy.App;
using Serpy.App.Platform;
using Serpy.App.Platform.Windows;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Health;
using Serpy.Core.Images;
using Serpy.Core.Operations;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;

bool isUnattended = args.Contains("--unattended", StringComparer.OrdinalIgnoreCase) ||
                    args.Contains("--fresh-install", StringComparer.OrdinalIgnoreCase);

if (isUnattended)
{
    ConsoleAttachment.TryAttachParent();
}

string singleInstanceMutexName = ResolveSingleInstanceMutexName();
Mutex singleInstanceMutex;
try
{
    singleInstanceMutex = ProcessController.AcquireOrTakeoverMutex(
        singleInstanceMutexName, isUnattended, msg => Console.WriteLine(msg));
}
catch (Exception ex)
{
    if (isUnattended)
    {
        Console.Error.WriteLine($"Error acquiring single instance lock: {ex.Message}");
        return 1;
    }
    // Single-instance guard for GUI: if another Serpy.App is already running, exit cleanly.
    return 0;
}

using (singleInstanceMutex)
{
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
            InstallerUrl     = manifest.Qemu.Windows.InstallerUrl,
            InstallerSha256  = manifest.Qemu.Windows.InstallerSha256,
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

    if (isUnattended)
    {
        return await RunUnattendedPipelineAsync(service, args);
    }

    return BuildAvaloniaApp(trayOnly, service)
        .StartWithClassicDesktopLifetime(args);
}

// ── Unattended execution pipeline ──────────────────────────────────────────
static async Task<int> RunUnattendedPipelineAsync(ApplianceService service, string[] args)
{
    Console.WriteLine("==================================================");
    Console.WriteLine("Serpy ERPNext Unattended Setup");
    Console.WriteLine("==================================================");

    var progress = new Progress<OperationUpdate>(update =>
    {
        if (!string.IsNullOrEmpty(update.Message))
        {
            Console.WriteLine($"[{update.Kind}] {update.Message}");
        }
    });

    var status = await service.GetStatusAsync();
    bool hasExistingArtifacts = status.Readiness != ReadinessState.NotBuilt ||
                                status.HasCommittedDataOnDisk ||
                                status.Health == HealthState.Running ||
                                File.Exists(Path.Combine(KnownPaths.ApplianceDir, "system.qcow2")) ||
                                File.Exists(Path.Combine(KnownPaths.ApplianceDir, ".staging-system.qcow2")) ||
                                File.Exists(Path.Combine(KnownPaths.ApplianceDir, "data.img"));

    if (hasExistingArtifacts)
    {
        Console.WriteLine("\n[1/4] Existing installation detected. Resetting appliance…");
        var resetResult = await service.ResetAsync(progress);
        if (resetResult.Outcome != OperationOutcome.Success)
        {
            Console.Error.WriteLine($"Reset failed: {resetResult.Message}");
            return 1;
        }
        Console.WriteLine("Reset complete.");
    }
    else
    {
        Console.WriteLine("\n[1/4] No existing appliance found. Starting clean install.");
    }

    status = await service.GetStatusAsync();
    if (status.Readiness == ReadinessState.NotBuilt)
    {
        Console.WriteLine("\n[2/4] Building system image…");
        var buildResult = await service.BuildAsync(progress);
        if (buildResult.Outcome != OperationOutcome.Success)
        {
            Console.Error.WriteLine($"Build failed: {buildResult.Message}");
            return 1;
        }
        Console.WriteLine("System image build succeeded.");
    }

    Console.WriteLine("\n[3/4] Initializing ERPNext data disk with default credentials…");
    const string defaultSite = "erp.serpy.local";
    const string defaultUser = "Administrator";
    const string defaultPassword = "admin";

    var initParams = new InitializationParameters(defaultSite, defaultPassword);
    var initResult = await service.InitializeAsync(initParams, progress);
    if (initResult.Outcome != OperationOutcome.Success)
    {
        Console.Error.WriteLine($"Initialization failed: {initResult.Message}");
        return 1;
    }
    Console.WriteLine("ERPNext initialization succeeded.");

    Console.WriteLine("\n[4/4] Starting appliance and verifying health…");
    var startResult = await service.StartAsync(progress);
    if (startResult.Outcome != OperationOutcome.Success)
    {
        Console.Error.WriteLine($"Startup failed: {startResult.Message}");
        return 1;
    }

    // Wait for loopback URL and running health
    string? loopbackUrl = null;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    while (!cts.IsCancellationRequested)
    {
        status = await service.GetStatusAsync();
        if (status.Health == HealthState.Running && !string.IsNullOrEmpty(status.LoopbackUrl))
        {
            loopbackUrl = status.LoopbackUrl;
            break;
        }
        if (status.Health is HealthState.Crashed or HealthState.Stopped)
        {
            Console.Error.WriteLine($"Appliance unexpectedly entered {status.Health} state.");
            return 1;
        }
        await Task.Delay(1000, cts.Token).ConfigureAwait(false);
    }

    if (string.IsNullOrEmpty(loopbackUrl))
    {
        Console.Error.WriteLine("Timeout waiting for appliance to become healthy.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("==================================================");
    Console.WriteLine("Serpy ERPNext Installation Complete!");
    Console.WriteLine("==================================================");
    Console.WriteLine($"URL:      {loopbackUrl}");
    Console.WriteLine($"Username: {defaultUser}");
    Console.WriteLine($"Password: {defaultPassword}");
    Console.WriteLine("==================================================");
    Console.WriteLine("Opening default browser…");

    new BrowserLauncher().OpenOnce(loopbackUrl);

    Console.WriteLine("Serpy will continue running in the background system tray.");
    Console.WriteLine("Press Ctrl+C in this terminal or exit from the system tray icon to stop.");
    Console.WriteLine();

    // Start Avalonia desktop host in tray-only mode so VM remains alive
    return BuildAvaloniaApp(trayOnly: true, service)
        .StartWithClassicDesktopLifetime(args);
}

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
static string ResolveSingleInstanceMutexName()
{
    var appData = KnownPaths.AppDataRoot;
    using var sha = System.Security.Cryptography.SHA256.Create();
    var hash = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(appData)));
    return $"Local\\Serpy-App-{Environment.UserName}-{hash[..8]}";
}
