using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serpy.Core.Coordination;

namespace Serpy.Core.Images;

public static class ProvenanceRecordStore
{
    public static string GetProvenancePath(string archivePath) => archivePath + ".provenance";

    public static void Write(string archivePath, ProvenanceRecord record)
    {
        var json = JsonSerializer.Serialize(record, ApplianceStateJsonContext.Default.ProvenanceRecord);
        var bytes = Encoding.UTF8.GetBytes(json);
        var stored = OperatingSystem.IsWindows() ? DpapiEncrypt(bytes) : bytes;
        var provPath = GetProvenancePath(archivePath);
        File.WriteAllBytes(provPath, stored);
    }

    public static ProvenanceRecord? Read(string archivePath)
    {
        var provPath = GetProvenancePath(archivePath);
        if (!File.Exists(provPath)) return null;
        try
        {
            var stored = File.ReadAllBytes(provPath);
            var bytes = OperatingSystem.IsWindows() ? DpapiDecrypt(stored) : stored;
            return JsonSerializer.Deserialize(bytes, ApplianceStateJsonContext.Default.ProvenanceRecord);
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiEncrypt(byte[] data) =>
        ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiDecrypt(byte[] data) =>
        ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}
