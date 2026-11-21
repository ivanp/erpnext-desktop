using System.Security.Cryptography;
using Serpy.Core.Configuration;
using Serpy.Core.Downloads;
using Serpy.Core.Versions;

namespace Serpy.Core.Images;

/// <summary>
/// Downloads and verifies the Debian genericcloud base image via DownloadEngine.
/// Reuses an existing verified copy; re-downloads only if absent or hash fails.
/// The URI and expected SHA come from the version manifest (publisher-signed source),
/// so a tampered config/versions.yaml cannot redirect the OS download.
/// </summary>
public sealed class BaseImageDownloader
{
    private readonly VersionManifest _manifest;
    private readonly DownloadEngine? _engine;
    private readonly HttpMessageHandler? _httpHandler;

    public string CachedImagePath => Path.Combine(
        KnownPaths.ApplianceDir,
        $"debian-base-{_manifest.DebianCloudImage.Version}.qcow2");

    public BaseImageDownloader(VersionManifest manifest, DownloadEngine? engine = null, HttpMessageHandler? httpHandler = null)
    {
        _manifest = manifest;
        _engine = engine;
        _httpHandler = httpHandler;
    }

    public BaseImageDownloader(VersionManifest manifest, HttpMessageHandler? httpHandler)
        : this(manifest, null, httpHandler)
    {
    }

    /// <summary>
    /// Ensure the verified base image exists.
    /// Downloads if absent; verifies the publisher-provided SHA-512 before returning.
    /// </summary>
    public async Task EnsureAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (File.Exists(CachedImagePath))
        {
            progress?.Report("Verifying cached Debian base image…");
            if (VerifySha512(CachedImagePath, _manifest.DebianCloudImage.Sha512))
            {
                progress?.Report("Cached image verified. Skipping download.");
                return;
            }

            progress?.Report("Cached image SHA-512 mismatch — re-downloading.");
            File.Delete(CachedImagePath);

            // An OS image that fails its check while a system image exists makes the Build step run again with data preserved.
            InvalidateExistingSystemImage();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(CachedImagePath)!);

        var osKey = Path.GetFileName(new Uri(_manifest.DebianCloudImage.Url).LocalPath);
        if (string.IsNullOrEmpty(osKey))
            osKey = $"debian-base-{_manifest.DebianCloudImage.Version}.qcow2";

        var engine = _engine ?? CreateThinEngine(osKey);

        progress?.Report($"Downloading Debian {_manifest.DebianCloudImage.Version} cloud image…");

        var engineProgress = new Progress<PartDownloadSnapshot>(snap =>
        {
            if (snap.State == PartDownloadState.Downloading)
                progress?.Report($"Downloading base image: {snap.Bytes / (1024 * 1024)} MB…");
        });

        await engine.DownloadPartsAsync(["os"], engineProgress, ct).ConfigureAwait(false);

        var cachedFile = engine.Cache.GetPath(osKey);
        if (!File.Exists(cachedFile) || !VerifySha512(cachedFile, _manifest.DebianCloudImage.Sha512))
        {
            throw new InvalidDataException(
                $"Debian image SHA-512 mismatch. Expected: {_manifest.DebianCloudImage.Sha512}");
        }

        if (!string.Equals(Path.GetFullPath(cachedFile), Path.GetFullPath(CachedImagePath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(cachedFile, CachedImagePath, overwrite: true);
        }

        progress?.Report("Base image ready.");
    }

    private void InvalidateExistingSystemImage()
    {
        var applianceDir = KnownPaths.ApplianceDir;
        var systemImage = Path.Combine(applianceDir, "system.qcow2");
        if (File.Exists(systemImage))
        {
            try { File.Delete(systemImage); } catch { }
        }

        var systemManifest = Path.Combine(applianceDir, SystemImageManifest.FileName);
        if (File.Exists(systemManifest))
        {
            try { File.Delete(systemManifest); } catch { }
        }
    }

    private DownloadEngine CreateThinEngine(string osKey)
    {
        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions
            {
                PythonLock = _manifest.Runtime.Python.Lock,
                NodeLock = _manifest.Runtime.Node.Lock,
                MariadbLock = _manifest.Runtime.MariaDb.Lock,
                RedisLock = _manifest.Runtime.Redis.Lock,
                FrappeCommit = _manifest.Apps.Frappe.Commit,
                ErpnextCommit = _manifest.Apps.ErpNext.Commit,
                FrappeLock = _manifest.Apps.Frappe.Lock,
                ErpnextLock = _manifest.Apps.ErpNext.Lock,
            },
            Parts =
            [
                new InputPart
                {
                    Id = "os",
                    Title = "Operating system for ERPNext",
                    Files =
                    [
                        new InputFile
                        {
                            Key = osKey,
                            Path = "",
                            Size = 0,
                            Sha512 = _manifest.DebianCloudImage.Sha512,
                            Urls = [_manifest.DebianCloudImage.Url],
                            InGuest = false
                        }
                    ]
                }
            ]
        };

        var cache = new DownloadCache();
        return new DownloadEngine(inputs, cache, _httpHandler);
    }

    private static bool VerifySha512(string path, string expectedHex)
    {
        if (string.IsNullOrEmpty(expectedHex)) return false;
        using var sha = SHA512.Create();
        using var fs = File.OpenRead(path);
        var actual = Convert.ToHexString(sha.ComputeHash(fs));
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }
}
