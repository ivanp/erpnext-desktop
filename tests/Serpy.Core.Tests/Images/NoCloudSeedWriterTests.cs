using DiscUtils.Iso9660;
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
    public void Write_UserDataSpanningMultipleSectors_RoundTrips()
    {
        Directory.CreateDirectory(_dir);
        var iso = Path.Combine(_dir, "large-seed.iso");
        var userData = "#cloud-config\n" + new string('x', 8_192);

        new NoCloudSeedWriter(userData, "instance-id: large\n").Write(iso);

        Assert.True(NoCloudSeedWriter.Verify(iso).IsValid);
        Assert.Equal(userData, NoCloudSeedWriter.ReadUserData(iso));
    }


    [Fact]
    public void Write_ProducesSpecCorrectPrimaryVolumeDescriptor()
    {
        Directory.CreateDirectory(_dir);
        var iso = Path.Combine(_dir, "descriptor.iso");
        new NoCloudSeedWriter("user", "meta").Write(iso);
        var bytes = File.ReadAllBytes(iso);
        const int pvd = 16 * 2048;

        Assert.Equal(10, ReadBothEndianInt32(bytes, pvd + 132));
        Assert.Equal(18, ReadLittleEndianInt32(bytes, pvd + 140));
        Assert.Equal(18, ReadBigEndianInt32(bytes, pvd + 144));
        Assert.Equal(19, ReadLittleEndianInt32(bytes, pvd + 148));
        Assert.Equal(19, ReadBigEndianInt32(bytes, pvd + 152));
        Assert.Equal(34, bytes[pvd + 156]); // root directory record begins here
    }

    private static int ReadBothEndianInt32(byte[] bytes, int offset)
    {
        var little = ReadLittleEndianInt32(bytes, offset);
        Assert.Equal(little, ReadBigEndianInt32(bytes, offset + 4));
        return little;
    }

    private static int ReadLittleEndianInt32(byte[] bytes, int offset) =>
        bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;

    private static int ReadBigEndianInt32(byte[] bytes, int offset) =>
        bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];

    [Fact]
    public void Write_IndependentReaderExposesLiteralNoCloudBasenames()
    {
        Directory.CreateDirectory(_dir);
        var iso = Path.Combine(_dir, "independent-reader.iso");
        new NoCloudSeedWriter("user", "meta").Write(iso);

        using var stream = File.OpenRead(iso);
        using var reader = new CDReader(stream, joliet: false);
        var names = reader.Root.GetFiles().Select(file => file.Name.Split(';')[0].ToLowerInvariant()).ToArray();

        Assert.Equal("cidata", reader.VolumeLabel, ignoreCase: true);
        Assert.Contains("user-data", names);
        Assert.Contains("meta-data", names);
    }

    [Fact]
    public void Write_RenderedProductionCloudInit_RoundTripsThroughIndependentReader()
    {
        Directory.CreateDirectory(_dir);
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cloud-init", "user-data"));
        var rendered = NoCloudSeedWriter.RenderUserData(
            template,
            ReadGuestAsset("provision-done.sh"),
            ReadGuestAsset("init-data.sh"),
            ReadGuestAsset("recover.sh"));
        var iso = Path.Combine(_dir, "production-seed.iso");

        new NoCloudSeedWriter(rendered, "instance-id: serpy\nlocal-hostname: serpy\n").Write(iso);

        using var stream = File.OpenRead(iso);
        using var reader = new CDReader(stream, joliet: false);
        var userData = reader.Root.GetFiles().Single(file => file.Name == "USER-DATA;1");
        using var content = userData.OpenRead();
        using var text = new StreamReader(content);
        Assert.Equal(rendered, text.ReadToEnd());
        Assert.True(new FileInfo(iso).Length > 23 * 2048);
    }

    [Fact]
    public void ProductionCloudInit_UsesImmutableRepositoriesAndLockedArtifacts()
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cloud-init", "user-data"));

        Assert.Contains("snapshot.debian.org/archive/debian/20260822T000000Z/ trixie main", template);
        Assert.Contains("snapshot.debian.org/archive/debian/20260822T000000Z/ sid main", template);
        Assert.Contains("archive.mariadb.org/mariadb-11.8.3", template);
        Assert.Contains("mariadb-server=1:11.8.3+maria~deb13", template);
        Assert.Contains("redis-server=5:8.0.2-3+deb13u2", template);
        Assert.Contains("node-v24.2.0-linux-x64.tar.xz", template);
        Assert.Contains("91a0794f4dbc94bc4a9296139ed9101de21234982bae2b325e37ebd3462273e5", template);
        Assert.DoesNotContain("deb.nodesource.com/setup_", template);
        Assert.DoesNotContain("downloads.mariadb.com/MariaDB/mariadb-11.8/repo", template);
    }

    [Fact]
    public void ProductionCloudInit_InstallsCurlBeforeMariaDbKeyDownload()
    {
        var template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cloud-init", "user-data"));
        var installCurl = template.IndexOf("apt-get install -y curl", StringComparison.Ordinal);
        var keyDownload = template.IndexOf("mariadb-keyring-2025.gpg", StringComparison.Ordinal);

        Assert.True(installCurl >= 0 && installCurl < keyDownload,
            "curl must be installed from the Debian snapshot before fetching the MariaDB signing key.");
    }

    private static string ReadGuestAsset(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "guest", fileName));
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

    [Fact]
    public void RenderUserData_CloudInitBlockContent_MatchesCanonicalScript()
    {
        // Template: six-space indented token under content: |
        const string template = """
            #cloud-config
            write_files:
              - path: /usr/local/bin/init-data.sh
                permissions: '0755'
                content: |
                  @@SERPY_INIT_DATA@@
            """;

        const string init = "#!/bin/bash\nset -euo pipefail\necho ready\n";

        var rendered = NoCloudSeedWriter.RenderUserData(template, "", init, "");

        // No unresolved tokens remain.
        Assert.DoesNotContain("@@SERPY_", rendered);

        // Parse the block scalar the same way cloud-init does:
        // strip leading block-indent, content must start with shebang at byte 0.
        var parsed = NoCloudSeedWriter.ExtractWriteFileContent(rendered, "/usr/local/bin/init-data.sh");
        Assert.NotNull(parsed);
        Assert.StartsWith("#!/bin/bash\n", parsed, StringComparison.Ordinal);
        Assert.Equal(init, parsed);
    }
}
