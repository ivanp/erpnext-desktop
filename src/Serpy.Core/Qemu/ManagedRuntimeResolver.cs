using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Serpy.Core.Configuration;

namespace Serpy.Core.Qemu;

/// <summary>
/// Downloads, verifies, and installs the managed QEMU runtime archive.
///
/// The archive is fetched into a per-user temporary directory, SHA-256 verified,
/// safely extracted, functionally probed, then atomically moved into the managed
/// runtime directory. No installer or elevation boundary participates.
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

    private const string ValidationMarkerFileName = ".serpy-runtime-validation";

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

        if (string.IsNullOrEmpty(manifest.Windows.ArchiveUrl))
            throw new InvalidOperationException(
                "No QEMU archive source configured. Set qemu.windows.archiveUrl " +
                "and qemu.windows.archiveSha256 in config/versions.yaml.");

        await InstallFromZipAsync(manifest.Windows.ArchiveUrl,
            manifest.Windows.ArchiveSha256, progress, ct);
    }

    public bool IsInstalled()
    {
        if (string.IsNullOrWhiteSpace(manifest.Windows.ArchiveSha256) || !Directory.Exists(BundleDir)) return false;
        foreach (var rel in RequiredExes)
            if (!File.Exists(Path.Combine(BundleDir, rel))) return false;
        foreach (var rel in RequiredFirmwarePaths)
            if (!File.Exists(Path.Combine(BundleDir, rel))) return false;
        return HasValidationMarker(BundleDir, manifest.QemuVersion, manifest.Windows.ArchiveSha256);
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
            var stagingShare = Path.Combine(stagingDir, "share", "qemu");

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
        public string ArchiveUrl       { get; set; } = string.Empty;
        public string ArchiveSha256    { get; set; } = string.Empty;
        public string SourceUrl        { get; set; } = string.Empty;
        public string LicenseNoticeUrl { get; set; } = string.Empty;
    }
}
