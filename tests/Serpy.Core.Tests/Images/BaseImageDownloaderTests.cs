using Serpy.Core.Configuration;

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Serpy.Core.Images;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Images;

[Collection("LifecycleLock")]
public sealed class BaseImageDownloaderTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyBaseImg-{Guid.NewGuid():N}");

    public BaseImageDownloaderTests()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_dir, "appdata"));
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }
    [Fact]
    public async Task EnsureAsync_AcceptsMatchingPublisherSha512Download()
    {
        var payload = Encoding.UTF8.GetBytes("verified Debian base image");
        var manifest = new VersionManifest
        {
            DebianCloudImage = new VersionManifest.DebianCloudImageSection
            {
                Version = $"test-{Guid.NewGuid():N}",
                Url = "https://publisher.invalid/debian.qcow2",
                Sha512 = Convert.ToHexString(SHA512.HashData(payload)),
            },
        };
        var downloader = new BaseImageDownloader(manifest, new StubHttpMessageHandler(payload));

        try
        {
            await downloader.EnsureAsync();

            Assert.Equal(payload, await File.ReadAllBytesAsync(downloader.CachedImagePath));
        }
        finally
        {
            DeleteIfExists(downloader.CachedImagePath);
            DeleteIfExists(downloader.CachedImagePath + ".tmp");
        }
    }

    [Fact]
    public void CachedImagePath_StaysAtApplianceDirectoryPath()
    {
        var manifest = new VersionManifest
        {
            DebianCloudImage = new VersionManifest.DebianCloudImageSection
            {
                Version = "20260819-2575",
                Url = "https://cloud.debian.org/images/cloud/trixie/20260819-2575/debian-13-genericcloud-amd64-20260819-2575.qcow2",
                Sha512 = new string('0', 128),
            },
        };
        var downloader = new BaseImageDownloader(manifest);
        var expected = Path.Combine(KnownPaths.ApplianceDir, "debian-base-20260819-2575.qcow2");
        Assert.Equal(expected, downloader.CachedImagePath);
    }

    [Fact]
    public async Task EnsureAsync_FailingOsImageCheckWhileSystemImageExists_InvalidatesSystemImage()
    {
        var payload = Encoding.UTF8.GetBytes("fresh verified Debian base image");
        var manifest = new VersionManifest
        {
            DebianCloudImage = new VersionManifest.DebianCloudImageSection
            {
                Version = $"test-{Guid.NewGuid():N}",
                Url = "https://publisher.invalid/debian.qcow2",
                Sha512 = Convert.ToHexString(SHA512.HashData(payload)),
            },
        };
        var downloader = new BaseImageDownloader(manifest, new StubHttpMessageHandler(payload));

        var applianceDir = KnownPaths.ApplianceDir;
        Directory.CreateDirectory(applianceDir);
        var systemImage = Path.Combine(applianceDir, "system.qcow2");
        var systemManifest = Path.Combine(applianceDir, SystemImageManifest.FileName);

        Directory.CreateDirectory(Path.GetDirectoryName(downloader.CachedImagePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(systemImage)!);
        await File.WriteAllTextAsync(downloader.CachedImagePath, "corrupted base image");
        await File.WriteAllTextAsync(systemImage, "existing-system-image");
        await File.WriteAllTextAsync(systemManifest, "{}");

        try
        {
            await downloader.EnsureAsync();

            // The corrupt base image was re-downloaded with fresh payload
            Assert.Equal(payload, await File.ReadAllBytesAsync(downloader.CachedImagePath));

            // The existing system image and manifest were invalidated so Build step will rebuild
            Assert.False(File.Exists(systemImage));
            Assert.False(File.Exists(systemManifest));
        }
        finally
        {
            DeleteIfExists(downloader.CachedImagePath);
            DeleteIfExists(systemImage);
            DeleteIfExists(systemManifest);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed class StubHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
    }
}
