using System.Text.Json.Serialization;

namespace Serpy.Core.Versions;

public sealed class BuildInputs
{
    public const int CurrentSchemaVersion = 1;

    public static readonly IReadOnlyList<string> FixedPartIds =
    [
        "engine",
        "os",
        "database",
        "system-tools",
        "language-tools",
        "web-tools",
        "framework",
        "erpnext"
    ];

    public static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "snapshot.debian.org",
        "deb.debian.org",
        "archive.mariadb.org",
        "supplychain.mariadb.com",
        "nodejs.org",
        "files.pythonhosted.org",
        "pypi.org",
        "registry.yarnpkg.com",
        "registry.npmjs.org",
        "github.com",
        "codeload.github.com",
        "api.github.com",
        "qemu.weilnetz.de",
        "cloud.debian.org",
    };

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("versions")]
    public ApplianceVersions Versions { get; set; } = new();

    [JsonPropertyName("parts")]
    public List<InputPart> Parts { get; set; } = [];
}

public sealed class ApplianceVersions
{
    [JsonPropertyName("pythonLock")]
    public string PythonLock { get; set; } = string.Empty;

    [JsonPropertyName("nodeLock")]
    public string NodeLock { get; set; } = string.Empty;

    [JsonPropertyName("mariadbLock")]
    public string MariadbLock { get; set; } = string.Empty;

    [JsonPropertyName("redisLock")]
    public string RedisLock { get; set; } = string.Empty;

    [JsonPropertyName("frappeCommit")]
    public string FrappeCommit { get; set; } = string.Empty;

    [JsonPropertyName("erpnextCommit")]
    public string ErpnextCommit { get; set; } = string.Empty;

    [JsonPropertyName("frappeLock")]
    public string FrappeLock { get; set; } = string.Empty;

    [JsonPropertyName("erpnextLock")]
    public string ErpnextLock { get; set; } = string.Empty;
}

public sealed class InputPart
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<InputFile> Files { get; set; } = [];
}

public sealed class InputFile
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("sha512")]
    public string? Sha512 { get; set; }

    [JsonPropertyName("urls")]
    public List<string> Urls { get; set; } = [];

    [JsonPropertyName("inGuest")]
    public bool InGuest { get; set; }
}

[JsonSerializable(typeof(BuildInputs))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class BuildInputsJsonContext : JsonSerializerContext
{
}
