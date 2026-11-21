using System.Text.Json;
using System.Text.Json.Serialization;
using Serpy.Core.Coordination;
using Serpy.Core.Versions;

namespace Serpy.Core.Images;

/// <summary>
/// Written atomically after a successful build, alongside system.qcow2.
/// Lifecycle preflight (init/start/recover) rejects any system.qcow2 missing this file.
/// </summary>
public sealed class SystemImageManifest
{
    public const string FileName = ".system-accepted.json";

    [JsonPropertyName("serpyVersion")]
    public string SerpyVersion { get; set; } = "1";

    [JsonPropertyName("buildTimestamp")]
    public DateTimeOffset BuildTimestamp { get; set; }

    /// <summary>SHA-256 of the exact accepted system.qcow2 bytes.</summary>
    [JsonPropertyName("imageSha256")]
    public string ImageSha256 { get; set; } = string.Empty;

    [JsonPropertyName("versions")]
    public GuestVersionReport Versions { get; set; } = new();

    /// <summary>Verifies that this manifest attests to the supplied image bytes.</summary>
    public bool MatchesImageDigest(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(ImageSha256) || !File.Exists(imagePath)) return false;
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(imagePath);
        return string.Equals(
            Convert.ToHexString(sha.ComputeHash(stream)), ImageSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-attests an image after a successful host-controlled initialization or recovery changed
    /// its system configuration. The write is atomic, so a crash leaves either the old manifest or
    /// the complete new one; it never leaves a half JSON document.
    /// </summary>
    public void RefreshAndWrite(string imagePath)
    {
        ImageSha256 = ComputeSha256(imagePath);
        var directory = Path.GetDirectoryName(imagePath)
            ?? throw new InvalidOperationException($"System image has no parent directory: {imagePath}");
        var manifestPath = Path.Combine(directory, FileName);
        var temporaryPath = manifestPath + ".tmp";
        File.WriteAllText(temporaryPath,
            JsonSerializer.Serialize(this, ApplianceStateJsonContext.Default.SystemImageManifest));
        File.Move(temporaryPath, manifestPath, overwrite: true);
    }

    private static string ComputeSha256(string imagePath)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(imagePath);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>Loads the existing acceptance record and refreshes it after a controlled image update.</summary>
    public static void RefreshAcceptedDigest(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath)
            ?? throw new InvalidOperationException($"System image has no parent directory: {imagePath}");
        var manifestPath = Path.Combine(directory, FileName);
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath), ApplianceStateJsonContext.Default.SystemImageManifest)
            ?? throw new InvalidOperationException($"Acceptance manifest is empty: {manifestPath}");
        manifest.RefreshAndWrite(imagePath);
    }

    /// <summary>
    /// Checks the adjacent acceptance manifest and its digest before a lifecycle
    /// operation consumes a system image.
    /// </summary>
    public static bool IsAccepted(string systemImagePath)
    {
        var directory = Path.GetDirectoryName(systemImagePath);
        if (string.IsNullOrEmpty(directory)) return false;

        var manifestPath = Path.Combine(directory, FileName);
        if (!File.Exists(manifestPath)) return false;

        try
        {
            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath),
                ApplianceStateJsonContext.Default.SystemImageManifest);
            return manifest?.MatchesImageDigest(systemImagePath) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
