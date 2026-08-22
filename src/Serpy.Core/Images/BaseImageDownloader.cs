using System.Security.Cryptography;
using Serpy.Core.Configuration;
using Serpy.Core.Versions;

namespace Serpy.Core.Images;

/// <summary>
/// Downloads and verifies the Debian genericcloud base image.
/// Reuses an existing verified copy; re-downloads only if absent or hash fails.
/// The URI and expected SHA come from the version manifest (publisher-signed source),
/// so a tampered config/versions.yaml cannot redirect the OS download.
/// </summary>
public sealed class BaseImageDownloader(VersionManifest manifest, HttpMessageHandler? httpHandler = null)
{
    /// <summary>Path where the verified base image is cached.</summary>
    public string CachedImagePath => Path.Combine(
        KnownPaths.ApplianceDir,
        $"debian-base-{manifest.DebianCloudImage.Version}.qcow2");

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
            if (VerifySha512(CachedImagePath, manifest.DebianCloudImage.Sha512))
            {
                progress?.Report("Cached image verified. Skipping download.");
                return;
            }
            progress?.Report("Cached image SHA-512 mismatch — re-downloading.");
            File.Delete(CachedImagePath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(CachedImagePath)!);
        var tmp = CachedImagePath + ".tmp";
        try
        {
            progress?.Report($"Downloading Debian {manifest.DebianCloudImage.Version} cloud image…");
            using var http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
            using var resp = await http.GetAsync(
                manifest.DebianCloudImage.Url,
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(tmp);
            await body.CopyToAsync(file, ct);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }

        progress?.Report("Verifying downloaded image…");
        if (!VerifySha512(tmp, manifest.DebianCloudImage.Sha512))
        {
            File.Delete(tmp);
            throw new InvalidDataException(
                $"Debian image SHA-512 mismatch. Expected: {manifest.DebianCloudImage.Sha512}");
        }

        File.Move(tmp, CachedImagePath, overwrite: false);
        progress?.Report("Base image ready.");
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
