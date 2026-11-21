using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Serpy.Core.Contracts;
using Serpy.Core.Downloads;
using Serpy.Core.Images;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Images;

[Collection("LifecycleLock")]
public sealed class CacheTransportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cacheDir;

    public CacheTransportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SerpyCacheTransportTest-{Guid.NewGuid():N}");
        _cacheDir = Path.Combine(_tempDir, "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private (DownloadCache Cache, InputFile File) CreateCachedFile(
        string key,
        string path,
        byte[] content,
        bool inGuest = true)
    {
        var cache = new DownloadCache(_cacheDir);
        var filePath = cache.GetPath(key);
        File.WriteAllBytes(filePath, content);

        var sha256 = Convert.ToHexString(SHA256.HashData(content));
        var inputFile = new InputFile
        {
            Key = key,
            Path = path,
            Size = content.Length,
            Sha256 = sha256,
            InGuest = inGuest,
        };

        return (cache, inputFile);
    }

    [Fact]
    public void WriteTar_HoldsEveryListedFileAtRelativePath_WithSameBytes_AndSizeIsMultipleOf512()
    {
        var cache = new DownloadCache(_cacheDir);
        var inputs = new BuildInputs
        {
            Parts =
            [
                new InputPart
                {
                    Id = "system-tools",
                    Files =
                    [
                        CreateCachedFile("deb1", "debs/package1.deb", Encoding.UTF8.GetBytes("deb content 1")).File,
                        CreateCachedFile("deb2", "debs/package2.deb", Encoding.UTF8.GetBytes("deb content 2")).File,
                    ]
                },
                new InputPart
                {
                    Id = "tools",
                    Files =
                    [
                        CreateCachedFile("node", "tools/node.tar.xz", Encoding.UTF8.GetBytes("node tarball content")).File,
                        // Host-only file should NOT enter the transport disk
                        CreateCachedFile("engine", "engine/qemu.zip", Encoding.UTF8.GetBytes("qemu runtime"), inGuest: false).File,
                    ]
                }
            ]
        };

        var tarPath = Path.Combine(_tempDir, "test-transport.tar");
        var resultPath = CacheTransport.WriteTar(tarPath, inputs, cache);

        Assert.Equal(tarPath, resultPath);
        Assert.True(File.Exists(tarPath));

        var fileInfo = new FileInfo(tarPath);
        Assert.True(fileInfo.Length > 0);
        Assert.True(fileInfo.Length % 512 == 0, $"Tar file length {fileInfo.Length} is not a multiple of 512.");

        var extractedEntries = new Dictionary<string, byte[]>();
        using (var tarFileStream = File.OpenRead(tarPath))
        using (var reader = new TarReader(tarFileStream))
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) != null)
            {
                if (entry.DataStream is not null)
                {
                    using var ms = new MemoryStream();
                    entry.DataStream.CopyTo(ms);
                    extractedEntries[entry.Name] = ms.ToArray();
                }
            }
        }

        Assert.Equal(3, extractedEntries.Count);
        Assert.True(extractedEntries.ContainsKey("debs/package1.deb"));
        Assert.Equal("deb content 1", Encoding.UTF8.GetString(extractedEntries["debs/package1.deb"]));
        Assert.True(extractedEntries.ContainsKey("debs/package2.deb"));
        Assert.Equal("deb content 2", Encoding.UTF8.GetString(extractedEntries["debs/package2.deb"]));
        Assert.True(extractedEntries.ContainsKey("tools/node.tar.xz"));
        Assert.Equal("node tarball content", Encoding.UTF8.GetString(extractedEntries["tools/node.tar.xz"]));
        Assert.False(extractedEntries.ContainsKey("engine/qemu.zip"));
    }

    [Fact]
    public void WriteTar_PathLongerThan100Characters_SurvivesWriteAndRead()
    {
        var cache = new DownloadCache(_cacheDir);
        // Path length > 120 chars
        var longPath = "wheels/very_long_package_name_with_extra_segments_to_exceed_one_hundred_characters_for_tar_ustar_limit-1.0.0-cp314-cp314-linux_x86_64.whl";
        Assert.True(longPath.Length > 100);

        var content = Encoding.UTF8.GetBytes("wheel payload with long path");
        var (_, inputFile) = CreateCachedFile("long-pkg", longPath, content);

        var inputs = new BuildInputs
        {
            Parts =
            [
                new InputPart
                {
                    Id = "language-tools",
                    Files = [inputFile]
                }
            ]
        };

        var tarPath = Path.Combine(_tempDir, "long-path-transport.tar");
        CacheTransport.WriteTar(tarPath, inputs, cache);

        Assert.True(File.Exists(tarPath));
        Assert.True(new FileInfo(tarPath).Length % 512 == 0);

        using var tarFileStream = File.OpenRead(tarPath);
        using var reader = new TarReader(tarFileStream);
        var entry = reader.GetNextEntry();
        Assert.NotNull(entry);
        Assert.Equal(longPath, entry.Name);

        using var ms = new MemoryStream();
        entry.DataStream!.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public void WriteTar_CacheFileChangedAfterDownloadCheck_ThrowsChecksumMismatch_AndDeletesOutput()
    {
        var cache = new DownloadCache(_cacheDir);
        var originalBytes = Encoding.UTF8.GetBytes("valid original content");
        var (_, inputFile) = CreateCachedFile("corruptible", "wheels/pkg.whl", originalBytes);

        // Tamper with the cached file after metadata registration
        var diskPath = cache.GetPath("corruptible");
        File.WriteAllBytes(diskPath, Encoding.UTF8.GetBytes("tampered corrupted content"));

        var inputs = new BuildInputs
        {
            Parts =
            [
                new InputPart
                {
                    Id = "language-tools",
                    Files = [inputFile]
                }
            ]
        };

        var tarPath = Path.Combine(_tempDir, "corrupted-transport.tar");

        var ex = Assert.Throws<CacheTransportChecksumMismatchException>(
            () => CacheTransport.WriteTar(tarPath, inputs, cache));

        Assert.Equal(FailureCode.ChecksumMismatch, ex.Code);
        Assert.Equal("corruptible", ex.Key);
        Assert.False(File.Exists(tarPath), "Partial tar file must be deleted when checksum mismatch occurs.");
    }

    [Fact]
    public void CacheTransport_Dispose_DeletesTransportFile()
    {
        var cache = new DownloadCache(_cacheDir);
        var (_, inputFile) = CreateCachedFile("sample", "tools/sample.tgz", [1, 2, 3]);
        var inputs = new BuildInputs
        {
            Parts = [new InputPart { Id = "tools", Files = [inputFile] }]
        };

        var tarPath = Path.Combine(_tempDir, "disposable.tar");
        using (var transport = CacheTransport.Write(tarPath, inputs, cache))
        {
            Assert.True(File.Exists(tarPath));
            Assert.Equal(tarPath, transport.FilePath);
        }

        Assert.False(File.Exists(tarPath));
    }
}
