using Serpy.Core.Images;

namespace Serpy.Core.Tests.Images;

public sealed class NoCloudSeedWriterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"SerpySeedTest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Write_ProducesValidIso_LabelCidataAndBothFiles()
    {
        Directory.CreateDirectory(_dir);
        var iso = Path.Combine(_dir, "seed.iso");

        new NoCloudSeedWriter("#cloud-config\nhostname: serpy\n", "instance-id: test\n")
            .Write(iso);

        Assert.True(File.Exists(iso));
        Assert.True(new FileInfo(iso).Length > 0);

        var v = NoCloudSeedWriter.Verify(iso);
        Assert.True(v.LabelOk,     $"Expected label 'cidata', error: {v.Error}");
        Assert.True(v.HasUserData, $"user-data not found, error: {v.Error}");
        Assert.True(v.HasMetaData, $"meta-data not found, error: {v.Error}");
        Assert.True(v.IsValid);
    }

    [Fact]
    public void Write_UserDataContentRoundTrips()
    {
        Directory.CreateDirectory(_dir);
        var iso = Path.Combine(_dir, "seed.iso");
        const string userData = "#cloud-config\nhostname: serpy-test\n";

        new NoCloudSeedWriter(userData, "instance-id: i1\n").Write(iso);

        // ReadUserData handles the ISO 9660 uppercase + ;1 version suffix.
        var content = NoCloudSeedWriter.ReadUserData(iso);
        Assert.Equal(userData, content);
    }

    [Fact]
    public void Verify_OnNonIsoFile_ReturnsInvalidWithError()
    {
        Directory.CreateDirectory(_dir);
        var notIso = Path.Combine(_dir, "garbage.iso");
        File.WriteAllBytes(notIso, [0x00, 0x01, 0x02, 0x03]);

        var v = NoCloudSeedWriter.Verify(notIso);
        Assert.False(v.IsValid);
        Assert.NotNull(v.Error);
    }
}
