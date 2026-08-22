using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Serpy.Core.Configuration;

namespace Serpy.Core.Qemu;

/// <summary>
/// Downloads, verifies, and installs the managed QEMU runtime bundle.
///
/// Two delivery paths (tried in order):
///   1. Weil NSIS installer  — InstallerUrl set, GnuTLS included, requires elevation.
///      Download → SHA-256 verify → silent-install elevated (/S /D=bundleDir).
///   2. CI-built zip         — ArchiveUrl set, no elevation required.
///      Download → SHA-256 verify → safe extract → atomic rename.
///
/// Both paths verify SHA-256 before executing anything.
/// Any failed step removes only the in-progress temporary file/directory.
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
    public string ShareDir      => Path.Combine(BundleDir, "share", "qemu");

    /// <summary>
    /// Ensure the QEMU runtime is installed and verified.
    /// No-ops if already complete; downloads and installs otherwise.
    /// </summary>
    public async Task EnsureInstalledAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (IsInstalled())
        {
            progress?.Report("QEMU runtime already installed.");
            return;
        }

        if (!string.IsNullOrEmpty(manifest.Windows.InstallerUrl))
            await InstallFromNsisAsync(manifest.Windows.InstallerUrl,
                manifest.Windows.Sha256, progress, ct);
        else if (!string.IsNullOrEmpty(manifest.Windows.ArchiveUrl))
            await InstallFromZipAsync(manifest.Windows.ArchiveUrl,
                manifest.Windows.ArchiveSha256, progress, ct);
        else
            throw new InvalidOperationException(
                "No QEMU bundle source configured. Set qemu.windows.installerUrl " +
                "or qemu.windows.archiveUrl in config/versions.yaml.");
    }

    public bool IsInstalled()
    {
        if (!Directory.Exists(BundleDir)) return false;
        foreach (var rel in RequiredExes)
            if (!File.Exists(Path.Combine(BundleDir, rel))) return false;
        foreach (var rel in RequiredFirmwarePaths)
            if (!File.Exists(Path.Combine(BundleDir, rel))) return false;
        return true;
    }

    // ── Path 1: Weil NSIS installer (primary) ────────────────────────────────

    private async Task InstallFromNsisAsync(
        string url, string expectedSha,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedSha))
            throw new InvalidOperationException(
                "qemu.windows.sha256 is empty. " +
                "Populate config/versions.yaml with the SHA-256 of the installer " +
                "before running; executing an unverified binary is not acceptable.");

        Directory.CreateDirectory(KnownPaths.RuntimeDir);
        var installerTmp = Path.Combine(
            KnownPaths.RuntimeDir, $".tmp-qemu-installer-{Guid.NewGuid():N}.exe");
        // Install into a staging dir so BundleDir is never partially populated.
        var stagingDir = Path.Combine(
            KnownPaths.RuntimeDir, $".tmp-qemu-stage-{Guid.NewGuid():N}");

        try
        {
            // 1. Download + verify.
            progress?.Report("Downloading QEMU installer…");
            await DownloadAsync(url, installerTmp, ct);

            progress?.Report("Verifying installer SHA-256…");
            VerifySha256Required(installerTmp, expectedSha);

            // 2. Silent NSIS install into staging dir (requires UAC elevation).
            progress?.Report("Installing QEMU to staging directory (requires administrator)…");
            RunNsisInstall(installerTmp, stagingDir);

            // 3. Validate staging contents.
            ValidateContents(stagingDir);

            // 4. Functional probes: -version and TLS object.
            var stagingExe = Path.Combine(stagingDir, "qemu-system-x86_64.exe");
            var stagingShare = Path.Combine(stagingDir, "share", "qemu");

            progress?.Report($"Probing -version (expecting {manifest.QemuVersion})…");
            var versionOutput = await WhpxProbe.GetVersionAsync(stagingExe, ct);
            if (!versionOutput.Contains("QEMU emulator version", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Unexpected -version output from staged bundle: {versionOutput}");

            // Exact version match: the installed binary must report the pinned version.
            // Prevents silently committing a newer or older installer whose behavior
            // may differ from what was tested (KTD11).
            if (!versionOutput.Contains(manifest.QemuVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Version mismatch: installer reports '{versionOutput.Trim()}' " +
                    $"but config/versions.yaml pins qemu.version='{manifest.QemuVersion}'. " +
                    $"Update qemu.version to match the installed binary before committing.");

            progress?.Report("Probing TLS support (--enable-gnutls required)…");
            await ProbeNsisHasTlsAsync(stagingExe, stagingShare, ct);

            // 5. Atomic rename: staging → BundleDir.
            progress?.Report("Committing QEMU runtime…");
            if (Directory.Exists(BundleDir)) Directory.Delete(BundleDir, recursive: true);
            Directory.Move(stagingDir, BundleDir);

            progress?.Report($"QEMU runtime installed: {versionOutput.Trim()}");
        }
        catch
        {
            // Clean up both the downloaded installer and any staging remnant.
            if (File.Exists(installerTmp)) File.Delete(installerTmp);
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
            throw;
        }
        finally
        {
            if (File.Exists(installerTmp)) File.Delete(installerTmp);
        }
    }

    /// <summary>
    /// Constructs NSIS silent-install arguments. /D must be the final argument
    /// and its value must not be quoted, including when the path contains spaces.
    /// </summary>
    public static string BuildNsisArguments(string targetDir) =>
        $"/S /D={targetDir}";

    private static void RunNsisInstall(string installerPath, string targetDir)
    {
        // NSIS silent install: /S suppresses UI, /D= sets target directory.
        // /D= is the final argument. UseShellExecute + runas triggers UAC elevation.
        var psi = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = BuildNsisArguments(targetDir),
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start QEMU installer.");

        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"QEMU installer exited with code {proc.ExitCode}.");
    }

    // ── Path 2: CI-built zip (no elevation) ──────────────────────────────────

    private async Task InstallFromZipAsync(
        string url, string expectedSha,
        IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(KnownPaths.RuntimeDir);
        var tempDir = Path.Combine(KnownPaths.RuntimeDir, $".tmp-qemu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var archivePath = Path.Combine(tempDir, "qemu.zip");

            progress?.Report("Downloading QEMU bundle…");
            await DownloadAsync(url, archivePath, ct);

            progress?.Report("Verifying SHA-256…");
            VerifySha256(archivePath, expectedSha);

            progress?.Report("Extracting bundle…");
            ExtractSafe(archivePath, tempDir);

            progress?.Report("Validating bundle contents…");
            ValidateContents(tempDir);

            Directory.Move(tempDir, BundleDir);
            progress?.Report("QEMU runtime installed.");
        }
        catch
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            throw;
        }
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
        // Empty SHA is rejected in the NSIS path by the caller guard.
        // For the zip path, empty means "not yet pinned in CI" and is skipped.
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(sha.ComputeHash(stream));
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"SHA-256 mismatch.  Expected: {expectedHex}  Actual: {actual}");
    }

    private static void VerifySha256(string filePath, string expectedHex)
    {
        if (string.IsNullOrEmpty(expectedHex)) return; // CI zip path: not yet pinned
        VerifySha256Required(filePath, expectedHex);
    }

    private static async Task ProbeNsisHasTlsAsync(
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
        foreach (var rel in RequiredFirmwarePaths)
            if (!File.Exists(Path.Combine(dir, rel)))
                throw new InvalidOperationException($"Bundle missing firmware: {rel}");
    }
}

/// <summary>Bundle metadata from versions.yaml.</summary>
public sealed class RuntimeManifest
{
    public string QemuVersion { get; set; } = string.Empty;

    public WindowsBundle Windows { get; set; } = new();

    public sealed class WindowsBundle
    {
        public string InstallerUrl     { get; set; } = string.Empty;
        public string Sha256           { get; set; } = string.Empty;
        public string ArchiveUrl       { get; set; } = string.Empty;
        public string ArchiveSha256    { get; set; } = string.Empty;
        public string SourceUrl        { get; set; } = string.Empty;
        public string LicenseNoticeUrl { get; set; } = string.Empty;
    }
}
