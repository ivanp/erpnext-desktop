using System.Security.Cryptography;
using System.Text;
using Serpy.Core.Downloads;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Downloads;

[Collection("LifecycleLock")]
public sealed class DownloadCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyCacheTests-{Guid.NewGuid():N}");
    private readonly DownloadCache _cache;

    public DownloadCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _cache = new DownloadCache(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void KeyLongerThan100Characters_IsStoredAndFoundAgain()
    {
        var longKey = new string('a', 120) + "-package-name-v1.0.tar.gz";
        var path = _cache.GetPath(longKey);

        Assert.NotNull(path);
        Assert.StartsWith(_dir, path);
        Assert.Equal(path, _cache.GetPath(longKey)); // Deterministic
        Assert.True(Path.GetFileName(path).Length <= 100);

        File.WriteAllText(path, "content");
        Assert.True(_cache.Contains(longKey));
    }

    [Fact]
    public void DeleteLeftovers_DeletesPartFilesOnly()
    {
        var finishedFile = _cache.GetPath("finished.deb");
        var partFile1 = _cache.GetPartPath("pending1.deb");
        var partFile2 = Path.Combine(_dir, "pending2.deb.part");

        File.WriteAllText(finishedFile, "finished");
        File.WriteAllText(partFile1, "part1");
        File.WriteAllText(partFile2, "part2");

        _cache.DeleteLeftovers();

        Assert.True(File.Exists(finishedFile));
        Assert.False(File.Exists(partFile1));
        Assert.False(File.Exists(partFile2));
    }

    [Fact]
    public void TryVerify_MatchesSha256AndCachesResultForRun()
    {
        var payload = Encoding.UTF8.GetBytes("sample verified content");
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var file = new InputFile
        {
            Key = "sample.tar.gz",
            Size = payload.Length,
            Sha256 = sha256,
            Urls = ["https://pypi.org/sample.tar.gz"],
            InGuest = true
        };

        var targetPath = _cache.GetPath(file.Key);
        File.WriteAllBytes(targetPath, payload);

        Assert.False(_cache.IsVerified(file.Key));
        var verified = _cache.TryVerify(file);

        Assert.True(verified);
        Assert.True(_cache.IsVerified(file.Key));
        Assert.True(File.Exists(targetPath));

        // Tamper with file on disk after verification; since verified in this run, returns true without rehashing
        File.WriteAllText(targetPath, "tampered");
        Assert.True(_cache.TryVerify(file));
    }

    [Fact]
    public void TryVerify_MismatchedChecksum_DeletesFileAndReturnsFalse()
    {
        var payload = Encoding.UTF8.GetBytes("corrupted payload");
        var expectedSha256 = new string('0', 64);
        var file = new InputFile
        {
            Key = "bad.deb",
            Size = payload.Length,
            Sha256 = expectedSha256,
            Urls = ["https://deb.debian.org/bad.deb"],
            InGuest = true
        };

        var targetPath = _cache.GetPath(file.Key);
        File.WriteAllBytes(targetPath, payload);

        var verified = _cache.TryVerify(file);

        Assert.False(verified);
        Assert.False(File.Exists(targetPath));
        Assert.False(_cache.IsVerified(file.Key));
    }

    [Fact]
    public void TryVerify_MismatchedSize_DeletesFileAndReturnsFalse()
    {
        var payload = Encoding.UTF8.GetBytes("short");
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        var file = new InputFile
        {
            Key = "wrong-size.deb",
            Size = 99999, // list says 99999 bytes, file on disk is 5 bytes
            Sha256 = sha256,
            Urls = ["https://deb.debian.org/wrong-size.deb"],
            InGuest = true
        };

        var targetPath = _cache.GetPath(file.Key);
        File.WriteAllBytes(targetPath, payload);

        var verified = _cache.TryVerify(file, out var reason);

        Assert.False(verified);
        Assert.False(File.Exists(targetPath));
        Assert.NotNull(reason);
        Assert.Contains("Wrong size: expected 99999 bytes, got 5", reason);
    }
}
