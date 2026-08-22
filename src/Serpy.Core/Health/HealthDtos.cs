using System.Text.Json.Serialization;

namespace Serpy.Core.Health;

// ── Request/response DTOs ─────────────────────────────────────────────────────

internal sealed class LoginRequest
{
    [JsonPropertyName("usr")] public string Usr { get; set; } = string.Empty;
    [JsonPropertyName("pwd")] public string Pwd { get; set; } = string.Empty;
}

internal sealed class CreateToDoRequest
{
    [JsonPropertyName("subject")] public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("status")]  public string Status  { get; set; } = "Open";
}

internal sealed class FrappeDocResponse
{
    [JsonPropertyName("data")] public FrappeDocData? Data { get; set; }
}

internal sealed class FrappeDocData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

// ── Credential storage record ─────────────────────────────────────────────────

internal sealed class CredentialRecord
{
    [JsonPropertyName("site")] public string Site { get; set; } = string.Empty;
    [JsonPropertyName("pwd")]  public string Pwd  { get; set; } = string.Empty;
}

// ── Source-generated context ──────────────────────────────────────────────────

[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(CreateToDoRequest))]
[JsonSerializable(typeof(FrappeDocResponse))]
[JsonSerializable(typeof(CredentialRecord))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class HealthJsonContext : JsonSerializerContext { }
