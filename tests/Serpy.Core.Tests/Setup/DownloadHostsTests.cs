using Serpy.Core.Downloads;

using Serpy.Core.Images;
using Serpy.Core.Setup;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Setup;

public sealed class DownloadHostsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyHosts-{Guid.NewGuid():N}");

    public DownloadHostsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static VersionManifest Manifest(string imageUrl = "https://cloud.debian.org/images/cloud/trixie/x.qcow2") =>
        new() { DebianCloudImage = { Url = imageUrl } };

    [Fact]
    public void EverySiteTheBuildContacts_IsAskedOnce_WithTheImageSiteFirst()
    {
        var hosts = DownloadHosts.For(Manifest()).Select(u => u.Host).ToList();

        Assert.Equal("cloud.debian.org", hosts[0]);
        Assert.Contains("snapshot.debian.org", hosts);
        Assert.Contains("archive.mariadb.org", hosts);
        Assert.Contains("nodejs.org", hosts);
        Assert.Contains("pypi.org", hosts);
        Assert.Equal(hosts.Count, hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AnImageUrlThatIsNotHttps_IsNotAsked_SoThePcCheckNeverProbesAPlainHttpSite()
    {
        var hosts = DownloadHosts.For(Manifest("http://mirror.example/x.qcow2")).Select(u => u.Host).ToList();

        Assert.DoesNotContain("mirror.example", hosts);
        Assert.NotEmpty(hosts);
    }

    [Fact]
    public void WithAnAcceptedSystemImage_NoSiteIsStillNeeded_SoAnOfflinePcPasses()
    {
        File.WriteAllText(Path.Combine(_dir, "system.qcow2"), "image");
        File.WriteAllText(Path.Combine(_dir, SystemImageManifest.FileName), "{}");

        Assert.Empty(DownloadHosts.StillNeeded(Manifest(), _dir));
    }

    [Theory]
    [InlineData("system.qcow2")]
    [InlineData(".staging-system.qcow2")]
    public void AnImageWithoutItsManifest_StillNeedsTheSites(string imageName)
    {
        File.WriteAllText(Path.Combine(_dir, imageName), "image");

        Assert.NotEmpty(DownloadHosts.StillNeeded(Manifest(), _dir));
    }

    [Fact]
    public void AnEmptyFolder_NeedsEverySite()
    {
        Assert.Equal(DownloadHosts.For(Manifest()).Count, DownloadHosts.StillNeeded(Manifest(), _dir).Count);
    }

    [Fact]
    public void BuildInputs_DerivesUniqueHttpsHosts()
    {
        var inputs = new BuildInputs
        {
            Parts =
            [
                new InputPart
                {
                    Id = "part1",
                    Title = "Part 1",
                    Files =
                    [
                        new InputFile { Key = "f1", Urls = ["https://pypi.org/f1.whl", "https://snapshot.debian.org/f1.deb"] },
                        new InputFile { Key = "f2", Urls = ["https://pypi.org/f2.whl", "http://insecure.example/f2.deb"] }
                    ]
                }
            ]
        };

        var hosts = DownloadHosts.For(inputs).Select(u => u.Host).ToList();
        Assert.Contains("pypi.org", hosts);
        Assert.Contains("snapshot.debian.org", hosts);
        Assert.DoesNotContain("insecure.example", hosts);
        Assert.Equal(2, hosts.Count);
    }

    [Fact]
    public void BuildInputs_WhenAllFilesCached_StillNeededReturnsEmpty()
    {
        var cache = new DownloadCache(_dir);
        var payload = "cached content"u8.ToArray();
        var file = new InputFile
        {
            Key = "cached.deb",
            Size = payload.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
            Urls = ["https://deb.debian.org/cached.deb"]
        };
        File.WriteAllBytes(cache.GetPath(file.Key), payload);

        var inputs = new BuildInputs
        {
            Parts = [new InputPart { Id = "part", Title = "Part", Files = [file] }]
        };

        var stillNeeded = DownloadHosts.StillNeeded(inputs, _dir, cache);
        Assert.Empty(stillNeeded);
    }
}
