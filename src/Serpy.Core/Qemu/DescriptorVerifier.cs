using System.Security.Cryptography;
using System.Text;

namespace Serpy.Core.Qemu;

/// <summary>
/// The publisher-signed runtime descriptor payload (base KTD11; carried forward
/// for the QEMU installer under IR3/IKTD3). This is the <b>trusted</b> source of
/// the installer URL/SHA-256 — never a value read from the user-writable
/// <c>config/versions.yaml</c> or supplied by a caller.
/// </summary>
public sealed record RuntimeDescriptor(
    string QemuVersion,
    string InstallerUrl,
    string InstallerSha256,
    string SourceUrl,
    string LicenseNoticeUrl)
{
    /// <summary>
    /// Deterministic byte representation signed/verified over — a fixed
    /// newline-joined field order, not a JSON serializer's ordering (which is not
    /// guaranteed stable across .NET versions/platforms). Any field change
    /// invalidates a prior signature, by design.
    /// </summary>
    public byte[] CanonicalBytes() => Encoding.UTF8.GetBytes(string.Join('\n',
        "serpy-runtime-descriptor-v1", QemuVersion, InstallerUrl, InstallerSha256, SourceUrl, LicenseNoticeUrl));
}

/// <summary>Envelope pairing a <see cref="RuntimeDescriptor"/> with its detached signature.</summary>
public sealed record SignedRuntimeDescriptor(RuntimeDescriptor Descriptor, byte[] Signature);

/// <summary>
/// Verifies a <see cref="SignedRuntimeDescriptor"/> against the trusted publisher
/// public key using RSA-PSS/SHA-256. This is the authority the elevated install
/// bootstrapper (IR3/IKTD1) trusts for the QEMU installer's SHA-256 — a caller can
/// never supply or weaken the hash directly; it must come from a descriptor that
/// verifies against the build's configured <see cref="DescriptorTrustAnchor"/>.
/// </summary>
public static class DescriptorVerifier
{
    /// <summary>
    /// Verify a signed descriptor against a specific trusted public key (SPKI DER).
    /// Overload used by tests to exercise wrong-key/tampered-signature rejection
    /// without touching the build's configured trust anchor.
    /// </summary>
    public static bool TryVerify(SignedRuntimeDescriptor signed, byte[] trustedPublicKeySpki)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(trustedPublicKeySpki, out _);
        return rsa.VerifyData(
            signed.Descriptor.CanonicalBytes(),
            signed.Signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
    }

    /// <summary>
    /// Verify against this build's configured trust anchor
    /// (<see cref="DescriptorTrustAnchor"/> — injected at build time via
    /// <c>SerpyDescriptorTrustKey</c>, never a hardcoded source literal). Returns
    /// the verified <see cref="RuntimeDescriptor"/> only on success; throws
    /// <see cref="InvalidOperationException"/> otherwise — including when this
    /// build has no trust anchor configured at all, which must never be treated
    /// as an implicit pass. Never returns an unverified descriptor.
    /// </summary>
    public static RuntimeDescriptor VerifyOrThrow(SignedRuntimeDescriptor signed)
    {
        var trustedKeyBase64 = DescriptorTrustAnchor.PublicKeySpkiBase64
            ?? throw new InvalidOperationException(
                "No descriptor trust anchor is configured for this build " +
                "(SerpyDescriptorTrustKey was not set at build time). This build cannot " +
                "verify or trust any runtime descriptor and must not proceed with an " +
                "elevated QEMU install.");

        var trustedKey = Convert.FromBase64String(trustedKeyBase64);
        if (!TryVerify(signed, trustedKey))
            throw new InvalidOperationException(
                $"Runtime descriptor signature verification failed against the configured " +
                $"trust anchor (source: {DescriptorTrustAnchor.Source ?? "unknown"}). " +
                "Refusing to trust its installer URL/SHA-256.");
        return signed.Descriptor;
    }
}

/// <summary>
/// Offline signer for a <see cref="RuntimeDescriptor"/> — used only by the release
/// build/publishing pipeline (never by the running application or bootstrapper) to
/// produce the signature <see cref="DescriptorVerifier"/> checks.
/// </summary>
public static class DescriptorSigner
{
    public static byte[] Sign(RuntimeDescriptor descriptor, RSA privateKey) =>
        privateKey.SignData(
            descriptor.CanonicalBytes(),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);
}
