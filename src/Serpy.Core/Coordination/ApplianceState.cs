using System.Text.Json.Serialization;
using Serpy.Core.Contracts;

namespace Serpy.Core.Coordination;

/// <summary>
/// Durable state persisted to disk between app launches.
/// A "generation" uniquely identifies one QEMU lifecycle epoch
/// and is passed as the QEMU VM name so a crash can be reconciled.
/// </summary>
public sealed class ApplianceState
{
    public const string FileName = "state.json";
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("readiness")]
    public ReadinessState Readiness { get; set; } = ReadinessState.NotBuilt;

    [JsonPropertyName("health")]
    public HealthState Health { get; set; } = HealthState.Stopped;

    /// <summary>
    /// Incremented monotonically with each new start attempt.
    /// Passed as the QEMU VM name so a later launch can reconcile.
    /// </summary>
    [JsonPropertyName("generation")]
    public long Generation { get; set; }

    /// <summary>PID of the QEMU process when running or starting.</summary>
    [JsonPropertyName("qemuPid")]
    public int? QemuPid { get; set; }

    /// <summary>
    /// Process start time (UTC ticks) to distinguish PIDs across boots.
    /// Null when not running.
    /// </summary>
    [JsonPropertyName("qemuStartTimeTicks")]
    public long? QemuStartTimeTicks { get; set; }

    /// <summary>Ephemeral loopback port used by QMP chardev socket.</summary>
    [JsonPropertyName("qmpPort")]
    public int? QmpPort { get; set; }

    /// <summary>Ephemeral loopback port used by QGA virtio-serial chardev socket.</summary>
    [JsonPropertyName("qgaPort")]
    public int? QgaPort { get; set; }

    /// <summary>Ephemeral loopback port used by the serial console chardev socket.</summary>
    [JsonPropertyName("serialPort")]
    public int? SerialPort { get; set; }

    /// <summary>Host-side loopback URL for ERPNext when running.</summary>
    [JsonPropertyName("loopbackUrl")]
    public string? LoopbackUrl { get; set; }

    /// <summary>Absolute path to system.qcow2 in use.</summary>
    [JsonPropertyName("systemImagePath")]
    public string? SystemImagePath { get; set; }

    /// <summary>Absolute path to data.img in use.</summary>
    [JsonPropertyName("dataImagePath")]
    public string? DataImagePath { get; set; }

    /// <summary>Active operation kind if a mutating operation is in progress.</summary>
    [JsonPropertyName("activeOperation")]
    public OperationKind? ActiveOperation { get; set; }

    /// <summary>Path to the log file for the active or most recent operation.</summary>
    [JsonPropertyName("logPath")]
    public string? LogPath { get; set; }

    /// <summary>
    /// Recovery journal: set when a recover operation starts, cleared on success.
    /// Blocks normal start if non-null (an interrupted migration must be completed).
    /// </summary>
    [JsonPropertyName("recoveryJournal")]
    public RecoveryJournal? RecoveryJournal { get; set; }
}

/// <summary>
/// Durable record of a recovery operation's phases.
/// Written before mutation; updated after each completed phase.
/// An incomplete journal blocks start.
/// </summary>
public sealed class RecoveryJournal
{
    [JsonPropertyName("targetImagePath")]
    public string TargetImagePath { get; set; } = string.Empty;

    /// <summary>SHA-256 of the accepted system image before the replacement swap.</summary>
    [JsonPropertyName("sourceImageSha256")]
    public string SourceImageSha256 { get; set; } = string.Empty;

    [JsonPropertyName("targetMariaDbVersion")]
    public string TargetMariaDbVersion { get; set; } = string.Empty;

    [JsonPropertyName("targetFrappeVersion")]
    public string TargetFrappeVersion { get; set; } = string.Empty;

    [JsonPropertyName("diskSwapped")]
    public bool DiskSwapped { get; set; }

    [JsonPropertyName("mariaDbUpgraded")]
    public bool MariaDbUpgraded { get; set; }

    [JsonPropertyName("benchMigrated")]
    public bool BenchMigrated { get; set; }

    [JsonPropertyName("healthPassed")]
    public bool HealthPassed { get; set; }
}
