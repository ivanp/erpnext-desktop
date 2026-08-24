using System.Text.Json.Serialization;

namespace Serpy.Core.Qemu;

/// <summary>
/// On-disk envelope for a signed runtime descriptor (IR3/IKTD3): the
/// canonical descriptor fields plus its base64-encoded RSA-PSS/SHA-256
/// signature. Shipped as an MSI-installed, admin-only sibling file next to
/// the signed <c>Serpy.InstallerBootstrapper.exe</c> -- the same
/// delivery/tamper protection the helper executable itself relies on
/// (IR3/IKTD1): a non-admin user cannot rewrite either file in place once
/// installed to <c>%ProgramFiles%\Serpy</c>. Never trusted on its own; the
/// elevated helper always verifies it via <see cref="DescriptorVerifier.VerifyOrThrow"/>
/// against the build's compiled-in <see cref="DescriptorTrustAnchor"/> before
/// using anything it contains.
/// </summary>
public sealed record SignedRuntimeDescriptorFile(
    [property: JsonPropertyName("descriptor")] RuntimeDescriptor Descriptor,
    [property: JsonPropertyName("signatureBase64")] string SignatureBase64)
{
    /// <summary>File name expected next to the bootstrapper executable.</summary>
    public const string FileName = "runtime-descriptor.signed.json";

    public SignedRuntimeDescriptor ToSignedRuntimeDescriptor() =>
        new(Descriptor, Convert.FromBase64String(SignatureBase64));
}

/// <summary>
/// Source-generated JSON context for the signed descriptor file
/// (Native-AOT: no reflection-based serialization).
/// </summary>
[JsonSerializable(typeof(SignedRuntimeDescriptorFile))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class SignedRuntimeDescriptorFileJsonContext : JsonSerializerContext
{
}
