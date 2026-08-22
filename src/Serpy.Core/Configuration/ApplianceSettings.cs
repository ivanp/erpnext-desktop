using System.Text.Json.Serialization;

namespace Serpy.Core.Configuration;

/// <summary>
/// User-editable appliance settings, persisted as JSON in SettingsDir.
/// </summary>
public sealed class ApplianceSettings
{
    public const string FileName = "settings.json";

    /// <summary>Host loopback port for ERPNext. Default 18080.</summary>
    [JsonPropertyName("erpnextPort")]
    public int ErpNextPort { get; set; } = 18080;

    /// <summary>Memory (MB) allocated to the VM. Default 4096.</summary>
    [JsonPropertyName("memoryMb")]
    public int MemoryMb { get; set; } = 4096;

    /// <summary>CPU cores allocated to the VM. Default 2.</summary>
    [JsonPropertyName("cpuCores")]
    public int CpuCores { get; set; } = 2;

    /// <summary>
    /// When true, Serpy.App registers a Run-key entry to start in tray-only mode.
    /// The service layer validates the path before writing the Run key.
    /// </summary>
    [JsonPropertyName("startAtSignIn")]
    public bool StartAtSignIn { get; set; } = false;
}
