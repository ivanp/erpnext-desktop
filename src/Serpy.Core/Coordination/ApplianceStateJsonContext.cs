using System.Text.Json.Serialization;
using Serpy.Core.Contracts;
using Serpy.Core.Images;
using Serpy.Core.Versions;

namespace Serpy.Core.Coordination;

/// <summary>
/// Source-generated JSON serialization context for all persisted state types.
/// Required for Native-AOT compatibility (IL2026/IL3050).
/// </summary>
[JsonSerializable(typeof(ApplianceState))]
[JsonSerializable(typeof(RecoveryJournal))]
[JsonSerializable(typeof(SystemImageManifest))]
[JsonSerializable(typeof(GuestVersionReport))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ApplianceStateJsonContext : JsonSerializerContext
{
}
