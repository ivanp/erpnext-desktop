using System.Text.Json.Serialization;

namespace Serpy.Core.Qemu;

/// <summary>
/// First message the non-elevated app (pipe client) sends after connecting to
/// the elevated bootstrapper's (pipe server) authenticated pipe (IU1 step 7).
/// Proves possession of the single-use nonce the app itself generated and
/// passed to the helper as a launch argument; the server additionally
/// authenticates the connection's real token SID via impersonation before
/// trusting anything in this message.
/// </summary>
public sealed record BootstrapClientHello(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("installerPath")] string InstallerPath);

/// <summary>
/// Final message the elevated bootstrapper (pipe server) sends back to the app
/// (pipe client) once the copy/re-verify/launch/ACL-harden sequence completes
/// or fails. Mirrors <see cref="BootstrapLaunchResult"/> plus an optional
/// human-readable failure reason for the app's progress reporting.
/// </summary>
public sealed record BootstrapWireResult(
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("stagingDir")] string? StagingDir,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage);

/// <summary>
/// Source-generated JSON context for the bootstrap pipe handshake wire types
/// (Native-AOT: no reflection-based serialization). Shared by the app-side
/// pipe client and the elevated <c>Serpy.InstallerBootstrapper</c> pipe server.
/// </summary>
[JsonSerializable(typeof(BootstrapClientHello))]
[JsonSerializable(typeof(BootstrapWireResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class BootstrapWireJsonContext : JsonSerializerContext
{
}
