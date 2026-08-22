using System.Text.Json.Serialization;

namespace Serpy.Core.Protocols.Qga;

// ── Sync handshake ────────────────────────────────────────────────────────────

public sealed class QgaSyncRequest
{
    [JsonPropertyName("execute")]
    public string Execute { get; set; } = "guest-sync-delimited";

    [JsonPropertyName("arguments")]
    public QgaSyncArgs Arguments { get; set; } = new();
}

public sealed class QgaSyncArgs
{
    [JsonPropertyName("id")]
    public int Id { get; set; } = Random.Shared.Next();
}

// ── guest-exec ────────────────────────────────────────────────────────────────

public sealed class QgaExecRequest
{
    [JsonPropertyName("execute")]
    public string Execute { get; set; } = "guest-exec";

    [JsonPropertyName("arguments")]
    public QgaExecArgs Arguments { get; set; } = new();
}

public sealed class QgaExecArgs
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("arg")]
    public string[]? Arg { get; set; }

    [JsonPropertyName("env")]
    public string[]? Env { get; set; }

    [JsonPropertyName("input-data")]
    public string? InputData { get; set; }

    [JsonPropertyName("capture-output")]
    public bool CaptureOutput { get; set; } = true;
}

public sealed class QgaExecResult
{
    [JsonPropertyName("return")]
    public QgaExecReturn? Return { get; set; }
}

public sealed class QgaExecReturn
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }
}

// ── guest-exec-status ────────────────────────────────────────────────────────

public sealed class QgaExecStatusRequest
{
    [JsonPropertyName("execute")]
    public string Execute { get; set; } = "guest-exec-status";

    [JsonPropertyName("arguments")]
    public QgaExecStatusArgs Arguments { get; set; } = new();
}

public sealed class QgaExecStatusArgs
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }
}

public sealed class QgaExecStatusResult
{
    [JsonPropertyName("return")]
    public QgaExecStatusReturn? Return { get; set; }
}

public sealed class QgaExecStatusReturn
{
    [JsonPropertyName("exited")]
    public bool Exited { get; set; }

    [JsonPropertyName("exitcode")]
    public int ExitCode { get; set; }

    /// <summary>Base64-encoded stdout; may be larger than 64 KiB — decode without size assumption.</summary>
    [JsonPropertyName("out-data")]
    public string? OutData { get; set; }

    [JsonPropertyName("err-data")]
    public string? ErrData { get; set; }
}
