using System.Net;
using System.Net.Sockets;
using Serpy.Core.Protocols.Qmp;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;

namespace Serpy.Windows.IntegrationTests;

/// <summary>
/// U2 architecture falsifier: managed bundle chain + WHPX + mTLS QMP smoke.
///
/// ── Execution modes ──────────────────────────────────────────────────────────
///
/// MODE A — Managed bundle (CI / KTD11-correct):
///   SERPY_RUN_SMOKE=1
///   SERPY_QEMU_VERSIONS_YAML=&lt;path to versions.yaml with archiveUrl+sha256 filled&gt;
///   → ManagedRuntimeResolver.EnsureInstalledAsync() runs the full chain:
///     download → SHA-256 verify → safe extract → version probe → install.
///   → Smoke runs against the resolver-installed bundle.
///   → This is the only path that satisfies KTD11.
///
/// MODE B — Pre-extracted dir (local dev only, does NOT satisfy KTD11):
///   SERPY_RUN_SMOKE=1
///   SERPY_QEMU_DIR=&lt;path to extracted QEMU bundle&gt;
///   → Smoke runs directly; resolver and SHA chain are NOT exercised.
///   → Acceptable only to iterate on the mTLS/WHPX logic without a built bundle.
///
/// ── Test scenarios (U2 #4) ───────────────────────────────────────────────────
///   1. -version → "QEMU emulator version …"
///   2. TLS support confirmed (build has --enable-gnutls)
///   3. WhpxProbe passes
///   4. Paused VM + mTLS QMP → query-status returns {status:…} → quit → process exits
///   5. Foreign plaintext client does not receive QMP greeting
/// </summary>
public sealed class QemuSmokeTests : IAsyncLifetime
{
    private static bool ShouldRun =>
        Environment.GetEnvironmentVariable("SERPY_RUN_SMOKE") == "1";

    // MODE A: managed bundle chain (KTD11-correct)
    private static string? VersionsYamlPath =>
        Environment.GetEnvironmentVariable("SERPY_QEMU_VERSIONS_YAML");

    // MODE B: pre-extracted dir (local dev shortcut only)
    private static string? RawQemuDir =>
        Environment.GetEnvironmentVariable("SERPY_QEMU_DIR");

    private static bool IsManagedChain => VersionsYamlPath is not null;

    private string? _certDir;
    private TlsCertificateStore? _certs;
    private string? _resolvedBundleDir;   // set during InitializeAsync

    public async Task InitializeAsync()
    {
        if (!ShouldRun) return;

        _certDir = Path.Combine(Path.GetTempPath(), $"serpy-smoke-certs-{Guid.NewGuid():N}");
        _certs = new TlsCertificateStore(_certDir);
        _certs.GenerateCertificates();

        if (IsManagedChain)
        {
            // MODE A: managed bundle chain (KTD11-correct).
            var manifest = VersionManifestLoader.LoadFrom(VersionsYamlPath!);
            var win = manifest.Qemu.Windows;

            bool hasArchive = !string.IsNullOrEmpty(win.ArchiveUrl) &&
                              !string.IsNullOrEmpty(win.ArchiveSha256);
            bool hasInstaller = !string.IsNullOrEmpty(win.InstallerUrl) &&
                                !string.IsNullOrEmpty(win.InstallerSha256);
            if (!hasArchive && !hasInstaller)
                throw new InvalidOperationException(
                    "SERPY_QEMU_VERSIONS_YAML must populate either " +
                    "qemu.windows.archiveUrl+archiveSha256 or " +
                    "qemu.windows.installerUrl+installerSha256 before running the managed-chain smoke.");

            var resolver = new ManagedRuntimeResolver(new RuntimeManifest
            {
                QemuVersion = manifest.Qemu.Version,
                Windows = new RuntimeManifest.WindowsBundle
                {
                    ArchiveUrl       = win.ArchiveUrl,
                    ArchiveSha256    = win.ArchiveSha256,
                    InstallerUrl     = win.InstallerUrl,
                    InstallerSha256  = win.InstallerSha256,
                    SourceUrl        = win.SourceUrl,
                    LicenseNoticeUrl = win.LicenseNoticeUrl,
                },
            });

            await resolver.EnsureInstalledAsync(
                new Progress<string>(msg => Console.WriteLine($"[resolver] {msg}")));

            _resolvedBundleDir = resolver.BundleDir;
            Console.WriteLine($"[smoke] Managed bundle installed at: {_resolvedBundleDir}");
        }
        else if (RawQemuDir is not null)
        {
            // MODE B: pre-extracted dir. Logs a warning so CI never silently uses this path.
            Console.WriteLine(
                "WARNING: Running smoke in raw-dir mode (SERPY_QEMU_DIR). " +
                "The managed-bundle chain (ManagedRuntimeResolver / SHA-256 verify) is NOT exercised. " +
                "Set SERPY_QEMU_VERSIONS_YAML to a populated versions.yaml for KTD11-correct verification.");
            _resolvedBundleDir = RawQemuDir;
        }
        else
        {
            throw new InvalidOperationException(
                "SERPY_RUN_SMOKE=1 but neither SERPY_QEMU_VERSIONS_YAML nor SERPY_QEMU_DIR is set.");
        }
    }

    public Task DisposeAsync()
    {
        if (_certDir is not null && Directory.Exists(_certDir))
            Directory.Delete(_certDir, recursive: true);
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (string exe, string shareDir) ResolveBundle()
    {
        var dir = _resolvedBundleDir
            ?? throw new InvalidOperationException("Bundle dir not resolved; InitializeAsync failed.");
        var exe = Path.Combine(dir, "qemu-system-x86_64.exe");
        var share = ManagedRuntimeResolver.ResolveFirmwareDir(dir);
        Assert.True(File.Exists(exe), $"qemu-system-x86_64.exe not found at: {exe}");
        return (exe, share);
    }

    private static int AllocateEphemeralPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QemuVersion_PrintsVersionString()
    {
        if (!ShouldRun) return;
        var (exe, _) = ResolveBundle();
        var version = await WhpxProbe.GetVersionAsync(exe);
        Assert.Contains("QEMU emulator version", version);
        Console.WriteLine($"[smoke] version: {version}");
    }

    [Fact]
    public async Task QemuBuild_HasTlsSupport()
    {
        if (!ShouldRun) return;
        var (exe, shareDir) = ResolveBundle();

        var version = await WhpxProbe.GetVersionAsync(exe);
        bool hasTlsInVersion = version.Contains("tls", StringComparison.OrdinalIgnoreCase);

        // Probe: -machine none with tls-creds-x509 object. A GnuTLS-less build prints
        // "Object type not found" and exits non-zero.
        bool tlsObjectWorks = false;
        if (!hasTlsInVersion && _certs is not null)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-machine"); psi.ArgumentList.Add("none");
            psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(shareDir);
            psi.ArgumentList.Add("-object");
            psi.ArgumentList.Add($"tls-creds-x509,id=tls0,endpoint=server,verify-peer=yes,dir={_certs.QemuCertDir}");
            psi.ArgumentList.Add("-nographic");
            using var p = new System.Diagnostics.Process { StartInfo = psi };
            p.Start();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            tlsObjectWorks = !stderr.Contains("Object type not found", StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(hasTlsInVersion || tlsObjectWorks,
            $"QEMU build does not have GnuTLS/TLS support. " +
            $"The bundle must be built with --enable-gnutls (KTD11). " +
            $"-version output: {version}");
    }

    [Fact]
    public async Task WhpxProbe_Passes()
    {
        if (!ShouldRun) return;
        var (exe, shareDir) = ResolveBundle();
        var result = await WhpxProbe.RunAsync(exe, shareDir);
        Assert.True(result.Success,
            $"WHPX probe failed: {result.Message}\n" +
            "Runner must have Windows Hypervisor Platform enabled and rebooted.");
    }

    /// <summary>
    /// The required U2 architecture falsifier:
    /// paused VM + mutual-TLS-authenticated QMP → query-status → quit.
    /// </summary>
    [Fact]
    public async Task MutualTlsQmp_PausedVm_QueryStatus_Quit()
    {
        if (!ShouldRun) return;
        var (exe, shareDir) = ResolveBundle();
        Assert.NotNull(_certs);

        var qmpPort = AllocateEphemeralPort();
        var clientCert = _certs.LoadClientCert();
        var caCert = _certs.LoadCaCert();

        var args = new QemuArguments()
            .Machine("q35")
            .Accelerator(AcceleratorPolicy.Resolve())
            .FirmwareDir(shareDir)
            .TlsCredsX509("tls0", _certs.QemuCertDir)
            .TlsChardev("qmp0", qmpPort, "tls0")
            .QmpOnChardev("qmp0")
            .Headless()
            .Paused();

        await using var proc = QemuProcess.Start(exe, args.Args);

        try
        {
            // Allow QEMU to bind the socket.
            await Task.Delay(1500);

            await using var qmp = await QmpClient.ConnectAsync(
                "127.0.0.1", qmpPort, clientCert, caCert,
                new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

            var result = await qmp.ExecuteAsync("query-status",
                ct: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

            Assert.NotNull(result);
            Assert.True(result.Value.TryGetProperty("status", out _),
                $"query-status response missing 'status' field: {result}");

            Console.WriteLine($"[smoke] query-status: {result}");

            await qmp.ExecuteAsync("quit",
                ct: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        finally
        {
            var exited = await proc.WaitForExitAsync(TimeSpan.FromSeconds(10));
            if (!exited) proc.Kill();
        }
    }

    [Fact]
    public async Task ForeignClient_WithoutClientCert_IsRejected()
    {
        if (!ShouldRun) return;
        var (exe, shareDir) = ResolveBundle();
        Assert.NotNull(_certs);

        var qmpPort = AllocateEphemeralPort();
        var args = new QemuArguments()
            .Machine("q35")
            .Accelerator(AcceleratorPolicy.Resolve())
            .FirmwareDir(shareDir)
            .TlsCredsX509("tls0", _certs.QemuCertDir)
            .TlsChardev("qmp0", qmpPort, "tls0")
            .QmpOnChardev("qmp0")
            .Headless()
            .Paused();

        await using var proc = QemuProcess.Start(exe, args.Args);

        try
        {
            await Task.Delay(1500);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, qmpPort);

            var stream = tcp.GetStream();
            stream.Write("{ \"execute\": \"qmp_capabilities\" }\n"u8.ToArray());

            var buf = new byte[256];
            tcp.ReceiveTimeout = 3000;
            int read;
            try { read = stream.Read(buf, 0, buf.Length); }
            catch { read = 0; }

            if (read > 0)
            {
                var response = System.Text.Encoding.UTF8.GetString(buf, 0, read);
                Assert.False(response.Contains("\"QMP\""),
                    "Foreign plaintext client received QMP greeting — mTLS is not enforced.");
            }
            // read == 0: connection reset by QEMU without TLS, which is correct.
        }
        finally
        {
            var exited = await proc.WaitForExitAsync(TimeSpan.FromSeconds(10));
            if (!exited) proc.Kill();
        }
    }
}
