using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Serpy.Core.Configuration;

namespace Serpy.Core.Qemu;

/// <summary>
/// Resolves, verifies, and installs the managed QEMU runtime.
///
/// This type has two deliberately separate surfaces (IR4):
/// <list type="bullet">
/// <item><b>Resolution/detection</b> — <see cref="QemuSystemExe"/>, <see cref="QemuImgExe"/>,
/// <see cref="ShareDir"/>, <see cref="IsInstalled"/> — side-effect-free, non-elevating.
/// Every lifecycle operation (build/init/start/recover) depends only on this surface.</item>
/// <item><b>Installing</b> — <see cref="InstallAsync"/> — the only elevating entry point.
/// It is reachable only through the consented setup flow (IU2), never a lifecycle operation.</item>
/// </list>
/// For the NSIS delivery path, elevation happens in the separate signed
/// <c>Serpy.InstallerBootstrapper.exe</c> (IR3/IKTD1), launched through
/// <see cref="BootstrapLauncher"/>; this resolver never launches the vendor installer
/// directly. On a zero exit it validates the returned staging tree
/// (<c>ValidateContents</c>/<c>-version</c>/TLS) and only then commits.
/// </summary>
public sealed class ManagedRuntimeResolver(RuntimeManifest manifest)
{
    private static readonly string[] RequiredExes =
    [
        "qemu-system-x86_64.exe",
        "qemu-img.exe",
    ];

    private static readonly string[] RequiredFirmwarePaths =
    [
        Path.Combine("share", "qemu", "bios-256k.bin"),
    ];

    /// <summary>Resolved path to the installed QEMU bundle directory.</summary>
    public string BundleDir => Path.Combine(
        KnownPaths.RuntimeDir, $"qemu-{manifest.QemuVersion}");

    public string QemuSystemExe => Path.Combine(BundleDir, "qemu-system-x86_64.exe");
    public string QemuImgExe    => Path.Combine(BundleDir, "qemu-img.exe");
    public string ShareDir      => ResolveFirmwareDir(BundleDir);

    private const string ValidationMarkerFileName = ".serpy-runtime-validation";

    internal string DeliveryFingerprint => !string.IsNullOrWhiteSpace(manifest.Windows.InstallerSha256)
        ? manifest.Windows.InstallerSha256
        : manifest.Windows.ArchiveSha256;

    /// <summary>
    /// Delegate seam for launching the elevated install bootstrapper. Defaults to
    /// <see cref="DefaultBootstrapLauncher"/> (real <c>runas</c> launch of
    /// <c>Serpy.InstallerBootstrapper.exe</c>). Tests substitute this to exercise
    /// accept/decline/failure without a real UAC prompt. Returns the helper's exit
    /// code and reported staging directory, or throws
    /// <see cref="System.ComponentModel.Win32Exception"/> on UAC decline (mirrors
    /// <c>WhpxEnabler.EnableAndRequestRestart</c>).
    /// </summary>
    internal Func<BootstrapRequest, CancellationToken, Task<BootstrapLaunchResult>> BootstrapLauncher { get; set; }
        = DefaultBootstrapLauncher;

    /// <summary>
    /// Install the QEMU runtime. This is the <b>only</b> elevating entry point on this
    /// type — call only from the consented setup flow (IU2), never a lifecycle operation.
    /// No-ops (returns <see cref="InstallOutcome.AlreadyInstalled"/>) if already complete.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (IsInstalled())
        {
            progress?.Report("QEMU runtime already installed.");
            return InstallOutcome.AlreadyInstalled;
        }

        if (!string.IsNullOrWhiteSpace(manifest.Windows.InstallerUrl))
            return await InstallFromNsisAsync(manifest.Windows.InstallerUrl,
                manifest.Windows.InstallerSha256, progress, ct);

        if (string.IsNullOrEmpty(manifest.Windows.ArchiveUrl))
            throw new InvalidOperationException(
                "No QEMU archive or installer source configured. Set qemu.windows.installerUrl " +
                "or qemu.windows.archiveUrl and its matching SHA-256 in config/versions.yaml.");

        await InstallFromZipAsync(manifest.Windows.ArchiveUrl,
            manifest.Windows.ArchiveSha256, progress, ct);
        return InstallOutcome.Installed;
    }

    public bool IsInstalled()
    {
        if (string.IsNullOrWhiteSpace(DeliveryFingerprint) || !Directory.Exists(BundleDir)) return false;
        foreach (var rel in RequiredExes)
            if (!File.Exists(Path.Combine(BundleDir, rel))) return false;
        try { _ = ResolveFirmwareDir(BundleDir); }
        catch (InvalidOperationException) { return false; }
        return HasValidationMarker(BundleDir, manifest.QemuVersion, DeliveryFingerprint);
    }


    // ── Archive installation (no elevation) ─────────────────────────────────

    private async Task InstallFromZipAsync(
        string url, string expectedSha,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedSha))
            throw new InvalidOperationException(
                "qemu.windows.archiveSha256 is empty. " +
                "Populate config/versions.yaml with the SHA-256 of the archive " +
                "before running; extracting an unverified bundle is not acceptable.");

        Directory.CreateDirectory(KnownPaths.RuntimeDir);
        var tempRoot = Path.Combine(KnownPaths.RuntimeDir, $".tmp-qemu-{Guid.NewGuid():N}");
        var extractionDir = Path.Combine(tempRoot, "extracted");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var archivePath = Path.Combine(tempRoot, "qemu.zip");
            progress?.Report("Downloading QEMU bundle…");
            await DownloadAsync(url, archivePath, ct);

            progress?.Report("Verifying SHA-256…");
            VerifySha256Required(archivePath, expectedSha);

            progress?.Report("Extracting bundle…");
            Directory.CreateDirectory(extractionDir);
            ExtractSafe(archivePath, extractionDir);
            var stagingDir = ResolveBundleRoot(extractionDir);

            progress?.Report("Validating bundle contents…");
            ValidateContents(stagingDir);
            var stagingExe = Path.Combine(stagingDir, "qemu-system-x86_64.exe");
            var stagingShare = ResolveFirmwareDir(stagingDir);

            progress?.Report($"Probing -version (expecting {manifest.QemuVersion})…");
            var versionOutput = await WhpxProbe.GetVersionAsync(stagingExe, ct);
            if (!versionOutput.Contains("QEMU emulator version", StringComparison.OrdinalIgnoreCase) ||
                !versionOutput.Contains(manifest.QemuVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Version mismatch: archive reports '{versionOutput.Trim()}' " +
                    $"but config/versions.yaml pins qemu.version='{manifest.QemuVersion}'.");

            progress?.Report("Probing TLS support (--enable-gnutls required)…");
            await ProbeHasTlsAsync(stagingExe, stagingShare, ct);

            CommitValidatedBundle(stagingDir, BundleDir, manifest.QemuVersion, expectedSha);
            progress?.Report($"QEMU runtime installed: {versionOutput.Trim()}");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private async Task<InstallOutcome> InstallFromNsisAsync(
        string url, string expectedSha,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha))
            throw new InvalidOperationException(
                "qemu.windows.installerSha256 is empty. Extracting an unverified installer is not acceptable.");

        Directory.CreateDirectory(KnownPaths.RuntimeDir);
        var tempRoot = Path.Combine(KnownPaths.RuntimeDir, $".tmp-qemu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var installerPath = Path.Combine(tempRoot, "qemu-w64-setup.exe");
            progress?.Report("Downloading QEMU installer…");
            await DownloadAsync(url, installerPath, ct);
            progress?.Report("Verifying installer SHA-256…");
            VerifySha256Required(installerPath, expectedSha);

            // The vendor installer is launched only by the separate signed elevated
            // bootstrapper (IR3/IKTD1), never directly by this non-elevated app.
            // The bootstrapper copies to an admin-only path, re-verifies against the
            // signed descriptor it trusts, ACL-hardens a fresh staging tree to the
            // authenticated original-user SID, and reports that staging path back.
            var request = new BootstrapRequest(
                InstallerPath: installerPath,
                PipeName: $"Serpy.InstallerBootstrapper.{Guid.NewGuid():N}",
                Nonce: BootstrapProtocol.CreateNonce(),
                ClaimedOriginalUserSid: CurrentUserSid());

            progress?.Report("Waiting for administrator approval…");
            BootstrapLaunchResult result;
            try
            {
                result = await BootstrapLauncher(request, ct);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User declined UAC (mirrors WhpxEnabler.EnableAndRequestRestart).
                return InstallOutcome.ElevationDeclined;
            }

            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Elevated QEMU install failed with exit code {result.ExitCode}.");
            if (string.IsNullOrWhiteSpace(result.StagingDir) || !Directory.Exists(result.StagingDir))
                throw new InvalidOperationException(
                    "Elevated install reported success but returned no valid staging directory.");

            var stagingDir = result.StagingDir;
            progress?.Report("Validating installed contents…");
            ValidateContents(stagingDir);
            var stagingExe = Path.Combine(stagingDir, "qemu-system-x86_64.exe");
            var versionOutput = await WhpxProbe.GetVersionAsync(stagingExe, ct);
            if (!versionOutput.Contains("QEMU emulator version", StringComparison.OrdinalIgnoreCase) ||
                !versionOutput.Contains(manifest.QemuVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Version mismatch: installer reports '{versionOutput.Trim()}'.");
            await ProbeHasTlsAsync(stagingExe, ResolveFirmwareDir(stagingDir), ct);
            CommitValidatedBundle(stagingDir, BundleDir, manifest.QemuVersion, expectedSha);
            progress?.Report($"QEMU runtime installed: {versionOutput.Trim()}");
            return InstallOutcome.Installed;
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Elevated QEMU install is Windows-only.");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("Could not resolve the current user's SID.");
    }

    /// <summary>
    /// Real default launcher for the elevated install bootstrapper.
    ///
    /// Fails closed <b>before any elevation attempt</b> in two cases: the
    /// helper is not installed at its expected admin-only Program Files path
    /// (IR3/IKTD1), or this build has no configured publisher anchor
    /// (<see cref="HelperPublisherAnchor"/>) to verify it against. Only once
    /// <see cref="HelperSignatureVerifier"/> confirms the installed helper is
    /// Authenticode-signed by the expected publisher (IR7) does this launch
    /// it with <c>Verb="runas"</c>, then connects to the pipe the elevated
    /// helper hosts as server (<see cref="ElevatedInstallChannel"/>) to send
    /// the nonce/installer path and await its authenticated result.
    /// </summary>
    private static async Task<BootstrapLaunchResult> DefaultBootstrapLauncher(
        BootstrapRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Elevated QEMU install is Windows-only.");

        // Installed in its own subfolder, not flat alongside Serpy.App.exe:
        // both projects reference Serpy.Core and each ships its own copy of
        // Serpy.Core.dll (and other shared deps) in a framework-dependent
        // publish; a flat shared install directory would collide on those
        // filenames (harvested MSI components) and installing over one
        // another's dependency copies is exactly the kind of subtle bug
        // this separation avoids.
        var bootstrapperExePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Serpy", "Bootstrapper",
            "Serpy.InstallerBootstrapper.exe");

        if (!File.Exists(bootstrapperExePath))
            throw new NotSupportedException(
                "Elevated QEMU install is not available: the separate signed " +
                $"Serpy.InstallerBootstrapper.exe helper (IR3/IKTD1) is not installed at " +
                $"'{bootstrapperExePath}'. No UAC elevation was attempted.");

        var expectedSubject = HelperPublisherAnchor.ExpectedSubject
            ?? throw new InvalidOperationException(
                "No helper publisher anchor is configured for this build " +
                "(SerpyHelperPublisherSubject was not set at build time). This build cannot " +
                "verify Serpy.InstallerBootstrapper.exe's signature and must not elevate it.");

        var verification = HelperSignatureVerifier.Verify(bootstrapperExePath, expectedSubject);
        if (!verification.IsValid)
            throw new InvalidOperationException(
                $"Refusing to elevate Serpy.InstallerBootstrapper.exe: signature verification " +
                $"failed against expected publisher '{expectedSubject}' " +
                $"(source: {HelperPublisherAnchor.Source ?? "unknown"}). {verification.Message}");

        var psi = new ProcessStartInfo(bootstrapperExePath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        psi.ArgumentList.Add("--pipe-name");
        psi.ArgumentList.Add(request.PipeName);
        psi.ArgumentList.Add("--nonce");
        psi.ArgumentList.Add(request.Nonce);
        psi.ArgumentList.Add("--claimed-sid");
        psi.ArgumentList.Add(request.ClaimedOriginalUserSid);
        psi.ArgumentList.Add("--installer-path");
        psi.ArgumentList.Add(request.InstallerPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Serpy.InstallerBootstrapper.exe.");

        // Started concurrently with the exit-wait below: the elevated helper
        // only exits after it has already written its result to the pipe, so
        // connecting must not be sequenced after WaitForExitAsync (that would
        // guarantee the client never connects before the helper's own
        // internal wait-for-connection timeout elapses).
        var channelTask = ElevatedInstallChannel.ConnectAndAwaitResultAsync(
            request.PipeName, request.Nonce, request.InstallerPath, TimeSpan.FromSeconds(60), ct);

        await proc.WaitForExitAsync(ct);
        return await channelTask;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("file://localhost/", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = new Uri(url).LocalPath;
            await using var src = File.OpenRead(localPath);
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, ct);
            return;
        }

        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"QEMU URL must use https:// (or file:// for CI self-test). Got: {url}");

        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        await stream.CopyToAsync(file, ct);
    }

    private static void VerifySha256Required(string filePath, string expectedHex)
    {
        if (string.IsNullOrEmpty(expectedHex))
            throw new InvalidOperationException("A required SHA-256 value is empty.");

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(sha.ComputeHash(stream));
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"SHA-256 mismatch.  Expected: {expectedHex}  Actual: {actual}");
    }

    private static async Task ProbeHasTlsAsync(
        string exe, string shareDir, CancellationToken ct)
    {
        // Generate a throwaway cert dir for the probe.
        var certDir = Path.Combine(Path.GetTempPath(), $"serpy-tls-probe-{Guid.NewGuid():N}");
        var certStore = new TlsCertificateStore(certDir);
        try
        {
            certStore.GenerateCertificates();
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-machine"); psi.ArgumentList.Add("none");
            psi.ArgumentList.Add("-L"); psi.ArgumentList.Add(shareDir);
            psi.ArgumentList.Add("-object");
            psi.ArgumentList.Add(
                $"tls-creds-x509,id=probe0,endpoint=server,verify-peer=yes,dir={certStore.QemuCertDir}");
            psi.ArgumentList.Add("-nographic");
            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.Start();
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (stderr.Contains("Object type not found", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "QEMU bundle does not have GnuTLS/TLS support. " +
                    "The Weil build is expected to include it; this installer may be " +
                    "incomplete or corrupt.");
        }
        finally
        {
            if (Directory.Exists(certDir)) Directory.Delete(certDir, recursive: true);
        }
    }

    internal static bool HasValidationMarker(string bundleDir, string version, string archiveSha256)
    {
        var markerPath = Path.Combine(bundleDir, ValidationMarkerFileName);
        if (!File.Exists(markerPath)) return false;

        try
        {
            var fields = File.ReadAllLines(markerPath);
            return fields.Length == 2 &&
                string.Equals(fields[0], version, StringComparison.Ordinal) &&
                string.Equals(fields[1], archiveSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static void WriteValidationMarker(string bundleDir, string version, string archiveSha256)
    {
        var markerPath = Path.Combine(bundleDir, ValidationMarkerFileName);
        var temporaryPath = markerPath + ".tmp";
        File.WriteAllLines(temporaryPath, [version, archiveSha256]);
        File.Move(temporaryPath, markerPath, overwrite: true);
    }

    internal static void CommitValidatedBundle(
        string stagingDir,
        string bundleDir,
        string version,
        string archiveSha256,
        Action<string, string>? moveDirectory = null)
    {
        WriteValidationMarker(stagingDir, version, archiveSha256);
        moveDirectory ??= Directory.Move;
        var backupDir = bundleDir + ".replaced-" + Guid.NewGuid().ToString("N");
        var hadExistingBundle = Directory.Exists(bundleDir);

        if (hadExistingBundle) Directory.Move(bundleDir, backupDir);
        try
        {
            moveDirectory(stagingDir, bundleDir);
        }
        catch
        {
            if (hadExistingBundle && !Directory.Exists(bundleDir))
                Directory.Move(backupDir, bundleDir);
            throw;
        }

        if (hadExistingBundle) Directory.Delete(backupDir, recursive: true);
    }


    public static string ResolveFirmwareDir(string bundleDir)
    {
        var archiveLayout = Path.Combine(bundleDir, "share", "qemu");
        if (File.Exists(Path.Combine(archiveLayout, "bios-256k.bin"))) return archiveLayout;

        var installerLayout = Path.Combine(bundleDir, "share");
        if (File.Exists(Path.Combine(installerLayout, "bios-256k.bin"))) return installerLayout;

        throw new InvalidOperationException("Bundle missing firmware: bios-256k.bin");
    }
    internal static string ResolveBundleRoot(string extractionDir)
    {
        if (File.Exists(Path.Combine(extractionDir, RequiredExes[0])))
            return extractionDir;

        var entries = Directory.EnumerateDirectories(extractionDir).ToArray();
        if (entries.Length == 1 && File.Exists(Path.Combine(entries[0], RequiredExes[0])))
            return entries[0];

        throw new InvalidOperationException(
            "QEMU archive must contain the bundle at its root or in one top-level directory.");
    }

    private static void ExtractSafe(string archivePath, string destDir)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(name))
                throw new InvalidDataException($"Absolute path in archive: {name}");
            foreach (var part in name.Split('/'))
                if (part == "..") throw new InvalidDataException($"Traversal in archive: {name}");

            var fullDest = Path.GetFullPath(Path.Combine(destDir, name));
            var root = Path.GetFullPath(destDir) + Path.DirectorySeparatorChar;
            if (!fullDest.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                fullDest != Path.GetFullPath(destDir))
                throw new InvalidDataException($"Entry escapes destination: {name}");

            if (name.EndsWith('/')) Directory.CreateDirectory(fullDest);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                entry.ExtractToFile(fullDest, overwrite: true);
            }
        }
    }

    private void ValidateContents(string dir)
    {
        foreach (var rel in RequiredExes)
            if (!File.Exists(Path.Combine(dir, rel)))
                throw new InvalidOperationException($"Bundle missing: {rel}");
        _ = ResolveFirmwareDir(dir);
    }
}

/// <summary>Bundle metadata from versions.yaml.</summary>
public sealed class RuntimeManifest
{
    public string QemuVersion { get; set; } = string.Empty;

    public WindowsBundle Windows { get; set; } = new();

    public sealed class WindowsBundle
    {
        public string ArchiveUrl       { get; set; } = string.Empty;
        public string ArchiveSha256    { get; set; } = string.Empty;
        public string InstallerUrl     { get; set; } = string.Empty;
        public string InstallerSha256  { get; set; } = string.Empty;
        public string SourceUrl        { get; set; } = string.Empty;
        public string LicenseNoticeUrl { get; set; } = string.Empty;
    }
}

/// <summary>Typed outcome of <see cref="ManagedRuntimeResolver.InstallAsync"/>.</summary>
public enum InstallOutcome
{
    /// <summary>Runtime was already installed and verified; no elevation attempted.</summary>
    AlreadyInstalled,

    /// <summary>Runtime was downloaded, elevated-installed (or archive-installed), validated, and committed.</summary>
    Installed,

    /// <summary>The user declined the UAC elevation prompt. No partial runtime was left behind.</summary>
    ElevationDeclined,
}

/// <summary>
/// Request handed to the elevated install bootstrapper. Carries no expected-hash
/// field (the descriptor the bootstrapper trusts is authoritative — see IR3/IKTD3)
/// and no caller-supplied destination (the destination is derived from the
/// pipe-authenticated client SID inside the elevated helper, never from this
/// request's <see cref="ClaimedOriginalUserSid"/> alone).
/// </summary>
public sealed record BootstrapRequest(
    string InstallerPath,
    string PipeName,
    string Nonce,
    string ClaimedOriginalUserSid);

/// <summary>
/// Result reported by the elevated install bootstrapper over the authenticated
/// pipe: the process exit code, and — on success — the validated, ACL-hardened
/// staging directory for the non-elevated resolver to run
/// <c>ValidateContents</c>/<c>-version</c>/TLS against before committing.
/// </summary>
public sealed record BootstrapLaunchResult(int ExitCode, string? StagingDir);
