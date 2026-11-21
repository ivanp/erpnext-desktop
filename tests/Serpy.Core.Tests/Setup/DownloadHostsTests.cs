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
}
