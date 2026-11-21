using System.Text.Json.Serialization;
using Serpy.Core.Contracts;

namespace Serpy.Core.Configuration;

/// <summary>
/// Durable host <b>setup</b> state, persisted separately from the appliance
/// lifecycle <c>StateStore</c>.
///
/// This exists as its own small store deliberately: GitNexus impact analysis
/// rates the appliance <c>StateStore</c> HIGH blast-radius (many lifecycle
/// callers), so first-run setup state is kept here to avoid touching that surface.
///
/// Appliance state and its journals stay the authority for readiness and recovery. This file is
/// the authority only for what files cannot show (KTD7): the restart-pending flag, the
/// start-at-sign-in choice, the company data route and the last failure. Its per-step status is
/// for display.
/// </summary>
public sealed class SetupState
{
    public const string FileName = "setup-state.json";
    public const int CurrentSchemaVersion = 2;

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

    /// <summary>Whether Serpy starts at sign-in. Written to the sign-in entry only when setup is Ready (KTD15).</summary>
    [JsonPropertyName("startAtSignIn")]
    public bool StartAtSignIn { get; set; } = true;

    /// <summary>
    /// The company data route, decided once before Build and kept: a build with data preserved
    /// already marks readiness Initialized, which would hide the route afterwards (KTD11).
    /// </summary>
    [JsonPropertyName("companyDataRoute")]
    public CompanyDataRoute CompanyDataRoute { get; set; } = CompanyDataRoute.Undecided;

    /// <summary>Set when a run reaches the end of the Start step.</summary>
    [JsonPropertyName("finished")]
    public bool Finished { get; set; }

    [JsonPropertyName("lastFailure")]
    public FailureCode LastFailure { get; set; } = FailureCode.None;

    [JsonPropertyName("lastFailedStep")]
    public SetupStepId? LastFailedStep { get; set; }

    /// <summary>Step status for display. Files decide what runs; this only draws the list.</summary>
    [JsonPropertyName("steps")]
    public Dictionary<SetupStepId, SetupStepState> Steps { get; set; } = [];
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
