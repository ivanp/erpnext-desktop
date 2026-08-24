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
/// Result the elevated bootstrapper (pipe server) sends back to the app
/// (pipe client) at each phase of the handoff. Two distinct meanings by
/// context:
/// <list type="bullet">
/// <item>After the initial copy/re-verify/launch/reparse-scan sequence
/// (<see cref="ElevatedInstallChannel.ConnectAsync"/>'s returned session):
/// <see cref="ExitCode"/> 0 means the staging tree exists and is ready for
/// the caller's OWN validation (<c>ValidateContents</c>/<c>-version</c>/TLS)
/// -- but the requesting user is granted only read+execute on it at this
/// point, never write, so nothing running under that user's own SID (this
/// legitimate resolver call included) can mutate what is about to be
/// validated.</item>
/// <item>After <see cref="ElevatedInstallSession.FinalizeAsync"/> reports
/// the caller's validation outcome: this is the FINAL message. <see cref="ExitCode"/>
/// 0 means full ownership/write access was granted (only now, after
/// validation succeeded) and the caller may proceed to commit; nonzero means
/// either validation was reported as failed (caller declined) or the final
/// ACL grant itself failed -- either way the staging tree has already been
/// deleted by the helper before this message is sent.</item>
/// </list>
/// </summary>
public sealed record BootstrapWireResult(
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("stagingDir")] string? StagingDir,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage);

/// <summary>
/// Second-phase message the app sends back over the SAME still-open
/// connection once it has finished validating the read-only staging tree
/// the helper handed off (IR3): <see cref="Approve"/> true means validation
/// passed and the helper should grant the requesting user full
/// ownership/write access; false means validation failed and the helper
/// should delete the staging tree instead of ever making it writable. This
/// closes the window a single-shot "hand off already-writable, then exit"
/// protocol would leave open: anything running under the same user SID
/// could otherwise mutate the tree between the helper's handoff and the
/// caller's own validation finishing.
///
/// <see cref="Nonce"/> must equal the SAME nonce authenticated in the
/// original <see cref="BootstrapClientHello"/> -- the helper rejects a
/// mismatched nonce and rejects a SECOND finalize message on a connection
/// that has already been finalized once, so this message cannot be
/// replayed or reordered onto a different session.
/// </summary>
public sealed record BootstrapFinalizeRequest(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("approve")] bool Approve);

/// <summary>
/// Source-generated JSON context for the bootstrap pipe handshake wire types
/// (Native-AOT: no reflection-based serialization). Shared by the app-side
/// pipe client and the elevated <c>Serpy.InstallerBootstrapper</c> pipe server.
/// </summary>
[JsonSerializable(typeof(BootstrapClientHello))]
[JsonSerializable(typeof(BootstrapWireResult))]
[JsonSerializable(typeof(BootstrapFinalizeRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class BootstrapWireJsonContext : JsonSerializerContext
{
}
