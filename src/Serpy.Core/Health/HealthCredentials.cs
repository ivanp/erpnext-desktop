using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serpy.Core.Configuration;

namespace Serpy.Core.Health;

/// <summary>
/// Manages the ERPNext site admin credential for the health checker.
/// On Windows: DPAPI-encrypted (System.Security.Cryptography.ProtectedData).
/// Cross-platform fallback: plain UTF-8 bytes (file system ACL is the only protection).
///
/// The credential MUST NOT appear in:
///   - ApplianceState or any persisted JSON
///   - OperationUpdate messages or logs
///   - Guest command-line arguments (passed via stdin instead)
///   - Settings or UI state
/// </summary>
public sealed class HealthCredentials
{
    private readonly string _storePath;

    public HealthCredentials(string? storePath = null)
    {
        _storePath = storePath ??
            Path.Combine(KnownPaths.SettingsDir, ".health-cred");
    }

    /// <summary>Store the admin password, DPAPI-encrypted on Windows.</summary>
    public void Store(string siteName, string adminPassword)
    {
        var record = new CredentialRecord { Site = siteName, Pwd = adminPassword };
        var bytes  = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(record, HealthJsonContext.Default.CredentialRecord));

        var stored = OperatingSystem.IsWindows() ? DpapiEncrypt(bytes) : bytes;

        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        if (OperatingSystem.IsWindows() && File.Exists(_storePath))
            File.SetAttributes(_storePath, File.GetAttributes(_storePath) & ~FileAttributes.Hidden);
        File.WriteAllBytes(_storePath, stored);
        if (OperatingSystem.IsWindows()) TryHideFile(_storePath);
    }

    /// <summary>Retrieve the stored credential. Returns null if not stored.</summary>
    public (string SiteName, string AdminPassword)? Retrieve()
    {
        if (!File.Exists(_storePath)) return null;
        try
        {
            var stored = File.ReadAllBytes(_storePath);
            var bytes  = OperatingSystem.IsWindows() ? DpapiDecrypt(stored) : stored;
            var record = JsonSerializer.Deserialize(bytes, HealthJsonContext.Default.CredentialRecord);
            if (record is null) return null;
            return (record.Site, record.Pwd);
        }
        catch { return null; }
    }

    public void Clear()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    // ── DPAPI (Windows only via conditional call) ─────────────────────────────

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiEncrypt(byte[] data) =>
        ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiDecrypt(byte[] data) =>
        ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);

    private static void TryHideFile(string path)
    {
        try { new FileInfo(path).Attributes |= FileAttributes.Hidden; }
        catch { /* non-critical */ }
    }
}
