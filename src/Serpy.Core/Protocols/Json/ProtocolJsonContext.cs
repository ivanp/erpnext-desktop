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
// QMP and QGA reject a field that is present with a null value ("env": null is "expected: array"), so
// an optional field that is not set must be left out of the message, never written as null.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.KebabCaseLower,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class ProtocolJsonContext : JsonSerializerContext
{
}
