using System.Text.Json.Serialization;

namespace Serpy.Core.Configuration;

/// <summary>
/// Durable host <b>setup</b> state, persisted separately from the appliance
/// lifecycle <c>StateStore</c>.
///
/// This exists as its own small store deliberately: GitNexus impact analysis
/// rates the appliance <c>StateStore</c> HIGH blast-radius (many lifecycle
/// callers), so first-run setup state — chiefly the WHPX "restart required"
/// marker (IU2/IR2) — is kept here to avoid touching that surface.
/// </summary>
public sealed class SetupState
{
    public const string FileName = "setup-state.json";
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// True after a successful WHPX feature enablement that has not yet been
    /// confirmed live by a post-reboot <c>WhpxProbe</c>. Survives app exit and
    /// reboot; cleared only once the definitive probe confirms WHPX is active,
    /// so acknowledging the restart prompt without rebooting never reads as ready.
    /// </summary>
    [JsonPropertyName("restartRequired")]
    public bool RestartRequired { get; set; }
}

/// <summary>
/// Source-generated JSON context for host setup state (Native-AOT: IL2026/IL3050).
/// </summary>
[JsonSerializable(typeof(SetupState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SetupStateJsonContext : JsonSerializerContext
{
}
