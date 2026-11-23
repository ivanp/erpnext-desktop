using System.Security.Cryptography;
using System.Text.Json;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;
using Xunit;

namespace Serpy.InstallerBootstrapper.Tests;

public sealed class DescriptorGenerationTests
{
    private static string? FindRepoFile(string relativePath)
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && current is not null; i++, current = Path.GetDirectoryName(current))
        {
            var candidate = Path.Combine(current, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void VersionsYaml_CanBeLoadedAndSigned_AndPassesVerification()
    {
        var versionsPath = FindRepoFile("config/versions.yaml");
        var privateKeyPath = FindRepoFile(".local-signing/descriptor-signing-private.pem");
        if (versionsPath is null || privateKeyPath is null)
        {
            return; // Skip if local signing key is not present
        }

        var manifest = VersionManifestLoader.LoadFrom(versionsPath);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Qemu.Version));
        Assert.False(string.IsNullOrWhiteSpace(manifest.Qemu.Windows.InstallerUrl));
        Assert.False(string.IsNullOrWhiteSpace(manifest.Qemu.Windows.InstallerSha256));

        var descriptor = new RuntimeDescriptor(
            manifest.Qemu.Version,
            manifest.Qemu.Windows.InstallerUrl,
            manifest.Qemu.Windows.InstallerSha256,
            manifest.Qemu.Windows.SourceUrl,
            manifest.Qemu.Windows.LicenseNoticeUrl);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(privateKeyPath));

        var signature = DescriptorSigner.Sign(descriptor, rsa);
        var signedFile = new SignedRuntimeDescriptorFile(descriptor, Convert.ToBase64String(signature));

        var json = JsonSerializer.Serialize(
            signedFile,
            SignedRuntimeDescriptorFileJsonContext.Default.SignedRuntimeDescriptorFile);

        var deserialized = JsonSerializer.Deserialize(
            json,
            SignedRuntimeDescriptorFileJsonContext.Default.SignedRuntimeDescriptorFile);

        Assert.NotNull(deserialized);
        var verified = DescriptorVerifier.VerifyOrThrow(deserialized.ToSignedRuntimeDescriptor());
        Assert.Equal(descriptor.QemuVersion, verified.QemuVersion);
        Assert.Equal(descriptor.InstallerSha256, verified.InstallerSha256);
    }
}
