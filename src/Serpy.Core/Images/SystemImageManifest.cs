using System.Text.Json.Serialization;
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

    [JsonPropertyName("versions")]
    public GuestVersionReport Versions { get; set; } = new();
}
