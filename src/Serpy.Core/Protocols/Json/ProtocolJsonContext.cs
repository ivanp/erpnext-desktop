using System.Text.Json.Serialization;
using Serpy.Core.Protocols.Qmp;
using Serpy.Core.Protocols.Qga;

namespace Serpy.Core.Protocols.Json;

/// <summary>
/// Source-generated JSON context for all QMP/QGA wire types.
/// Required for Native-AOT: no reflection-based serialization.
/// </summary>
[JsonSerializable(typeof(QmpGreeting))]
[JsonSerializable(typeof(QmpCapabilities))]
[JsonSerializable(typeof(QmpRequest))]
[JsonSerializable(typeof(QmpResponse))]
[JsonSerializable(typeof(QmpEvent))]
[JsonSerializable(typeof(QmpQueryStatus))]
[JsonSerializable(typeof(QmpSystemPowerdown))]
[JsonSerializable(typeof(QgaSyncRequest))]
[JsonSerializable(typeof(QgaExecRequest))]
[JsonSerializable(typeof(QgaExecStatusRequest))]
[JsonSerializable(typeof(QgaExecResult))]
[JsonSerializable(typeof(QgaExecStatusResult))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.KebabCaseLower,
    UseStringEnumConverter = true)]
public sealed partial class ProtocolJsonContext : JsonSerializerContext
{
}
