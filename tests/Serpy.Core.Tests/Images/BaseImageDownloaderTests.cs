using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Serpy.Core.Images;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Images;

public sealed class BaseImageDownloaderTests
{
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
