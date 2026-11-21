using System.Security.Cryptography;
using Serpy.Core.Images;
using Serpy.Core.Operations;

namespace Serpy.Core.Tests.Images;

// RecordAcceptedBuild reads the app-data folder to learn whether committed data exists. Against the
// real folder, a data.img left by a real setup would make it record Initialized, so these tests use
// a temporary root, and the collection keeps the process-wide override away from other tests.
[Collection("LifecycleLock")]
public sealed class SystemImageManifestTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SerpyManifest-{Guid.NewGuid():N}");

    public SystemImageManifestTests() =>
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_directory, "appdata"));

    [Fact]
    public void MatchesImageDigest_AcceptsExactQcow2Bytes()
    {
        Directory.CreateDirectory(_directory);
        var image = Path.Combine(_directory, "system.qcow2");
        var bytes = "accepted image bytes"u8.ToArray();
        File.WriteAllBytes(image, bytes);
        var manifest = new SystemImageManifest
        {
            ImageSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        };

        Assert.True(manifest.MatchesImageDigest(image));
    }

    [Fact]
    public void RefreshAndWrite_UpdatesTheDigestAfterAControlledSystemChange()
    {
        Directory.CreateDirectory(_directory);
        var image = Path.Combine(_directory, "system.qcow2");
        File.WriteAllText(image, "changed by successful data initialization");
        var manifest = new SystemImageManifest
        {
            ImageSha256 = new string('0', 64),
            Versions = new Serpy.Core.Versions.GuestVersionReport
            {
                Python = "3.14.7",
                Node = "24.2.0",
                MariaDb = "11.8.3",
                Redis = "8.0.2",
                Frappe = "16.31.0",
                ErpNext = "16.32.3",
            },
        };

        manifest.RefreshAndWrite(image);

        Assert.True(SystemImageManifest.IsAccepted(image));
        Assert.Equal("3.14.7", manifest.Versions.Python);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))), manifest.ImageSha256);
    }

    [Fact]
    public void RefreshAndWrite_WritesBesideTheImage_NotToAnUnrelatedDirectory()
    {
        Directory.CreateDirectory(_directory);
        var image = Path.Combine(_directory, "system.qcow2");
        File.WriteAllText(image, "image");
        var manifest = new SystemImageManifest();

        manifest.RefreshAndWrite(image);

        Assert.True(File.Exists(Path.Combine(_directory, SystemImageManifest.FileName)));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(image)!, "system.json")));
    }

    [Fact]
    public void MatchesImageDigest_StillRejectsTheOldDigestAfterTheImageChanges()
    {
        Directory.CreateDirectory(_directory);
        var image = Path.Combine(_directory, "system.qcow2");
        File.WriteAllText(image, "original");
        var manifest = new SystemImageManifest
        {
            ImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))),
        };
        File.AppendAllText(image, "controlled update");

        Assert.False(manifest.MatchesImageDigest(image));
    }


    [Fact]
    public void MatchesImageDigest_RejectsLegacyManifestWithoutDigest()
    {
        Directory.CreateDirectory(_directory);
        var image = Path.Combine(_directory, "system.qcow2");
        File.WriteAllBytes(image, "accepted image bytes"u8.ToArray());

        Assert.False(new SystemImageManifest().MatchesImageDigest(image));
    }
    [Fact]
    public void SwappedRecovery_ValidatesCurrentDiskAgainstReplacementManifest()
    {
        Directory.CreateDirectory(_directory);
        var replacementImage = Path.Combine(_directory, "replacement.qcow2");
        var replacementBytes = "replacement image bytes"u8.ToArray();
        File.WriteAllBytes(replacementImage, replacementBytes);
        var replacement = new SystemImageManifest
        {
            ImageSha256 = Convert.ToHexString(SHA256.HashData(replacementBytes)),
        };
        var installed = new SystemImageManifest { ImageSha256 = new string('0', 64) };

        Assert.True(RecoverOperation.MatchesCurrentImageForRecovery(
            installed, replacement, diskSwapped: true, replacementImage));
    }

    [Fact]
    public void AcceptedBuildState_RecordsBuiltReadinessAndSystemPath()
    {
        Directory.CreateDirectory(_directory);
        var statePath = Path.Combine(_directory, "state.json");
        var systemImage = Path.Combine(_directory, "system.qcow2");
        var store = new Serpy.Core.Coordination.StateStore(statePath);

        BuildOperation.RecordAcceptedBuild(store, systemImage);

        var state = store.Read();
        Assert.Equal(Contracts.ReadinessState.Built, state.Readiness);
        Assert.Equal(Contracts.HealthState.Stopped, state.Health);
        Assert.Equal(systemImage, state.SystemImagePath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

public sealed class SystemImagePreflightTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SerpyPreflight-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void VerifyAcceptedImage_MissingManifest_RejectsSystemImage()
    {
        Directory.CreateDirectory(_directory);
        var image = Path.Combine(_directory, "system.qcow2");
        File.WriteAllText(image, "unattested image");

        Assert.False(SystemImageManifest.IsAccepted(image));
    }

    [Fact]
    public void VerifyAcceptedImage_ManifestDigestMismatch_RejectsSystemImage()
    {
        Directory.CreateDirectory(_directory);
        var image = Path.Combine(_directory, "system.qcow2");
        File.WriteAllText(image, "changed image");
        File.WriteAllText(Path.Combine(_directory, SystemImageManifest.FileName),
            "{\"imageSha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}");

        Assert.False(SystemImageManifest.IsAccepted(image));
    }
}
