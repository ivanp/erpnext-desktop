using System.Text.Json;
using System.Text.Json.Serialization;

namespace Serpy.Core.Protocols.Qmp;

// ── Greeting ────────────────────────────────────────────────────────────────

public sealed class QmpGreeting
{
    [JsonPropertyName("QMP")]
    public QmpGreetingBody? Qmp { get; set; }
}

public sealed class QmpGreetingBody
{
    [JsonPropertyName("version")]
    public JsonElement Version { get; set; }

    [JsonPropertyName("capabilities")]
    public JsonElement[] Capabilities { get; set; } = [];
}

// ── Capability handshake ─────────────────────────────────────────────────────

public sealed class QmpCapabilities
{
    [JsonPropertyName("execute")]
    public string Execute { get; set; } = "qmp_capabilities";
}

// ── Generic request / response ────────────────────────────────────────────────

public sealed class QmpRequest
{
    [JsonPropertyName("execute")]
    public string Execute { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("arguments")]
    public Dictionary<string, object?>? Arguments { get; set; }
}

public sealed class QmpResponse
{
    [JsonPropertyName("return")]
    public JsonElement? Return { get; set; }

    [JsonPropertyName("error")]
    public QmpError? Error { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public sealed class QmpError
{
    [JsonPropertyName("class")]
    public string Class { get; set; } = string.Empty;

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = string.Empty;
}

// ── Named commands ────────────────────────────────────────────────────────────

public sealed class QmpSystemPowerdown
{
    [JsonPropertyName("execute")]
    public string Execute { get; set; } = "system_powerdown";
}

public sealed class QmpQueryStatus
{
    [JsonPropertyName("execute")]
    public string Execute { get; set; } = "query-status";
}

// ── Event ─────────────────────────────────────────────────────────────────────

public sealed class QmpEvent
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public JsonElement Timestamp { get; set; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }
}
