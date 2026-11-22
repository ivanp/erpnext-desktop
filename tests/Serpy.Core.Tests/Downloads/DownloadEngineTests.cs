using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Serpy.Core.Contracts;
using Serpy.Core.Downloads;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Downloads;

[Collection("LifecycleLock")]
public sealed class DownloadEngineTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"SerpyDlEngineTests-{Guid.NewGuid():N}");
    private readonly DownloadCache _cache;

    public DownloadEngineTests()
    {
        Directory.CreateDirectory(_cacheDir);
        _cache = new DownloadCache(_cacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
        }
    }

    private static (InputFile File, byte[] Payload) CreateFile(string key, string url, string content = "file-content")
    {
        var payload = Encoding.UTF8.GetBytes(content);
        var sha = Convert.ToHexString(SHA256.HashData(payload));
        return (new InputFile
        {
            Key = key,
            Path = $"guest/{key}",
            Size = payload.Length,
            Sha256 = sha,
            Urls = [url],
            InGuest = true
        }, payload);
    }

    [Fact]
    public async Task ConcurrencyLimits_RespectsOverallAndSnapshotLimits()
    {
        var maxOverallObserved = 0;
        var maxSnapshotObserved = 0;
        var currentOverall = 0;
        var currentSnapshot = 0;
        var gate = new object();

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            var isSnapshot = req.RequestUri!.Host.Equals("snapshot.debian.org", StringComparison.OrdinalIgnoreCase);

            lock (gate)
            {
                currentOverall++;
                if (currentOverall > maxOverallObserved) maxOverallObserved = currentOverall;
                if (isSnapshot)
                {
                    currentSnapshot++;
                    if (currentSnapshot > maxSnapshotObserved) maxSnapshotObserved = currentSnapshot;
                }
            }

            // Yield to allow concurrent tasks to enter handler
            await Task.Delay(20, ct);

            lock (gate)
            {
                currentOverall--;
                if (isSnapshot) currentSnapshot--;
            }

            var content = "payload-" + req.RequestUri.AbsoluteUri;
            var bytes = Encoding.UTF8.GetBytes(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });

        // 6 files: 3 on snapshot.debian.org, 3 on deb.debian.org
        var parts = new List<InputPart>();
        var files = new List<InputFile>();
        for (var i = 0; i < 3; i++)
        {
            var url = $"https://snapshot.debian.org/pkg{i}.deb";
            var (file, _) = CreateFile($"snap{i}.deb", url, $"payload-{url}");
            files.Add(file);
        }
        for (var i = 0; i < 3; i++)
        {
            var url = $"https://deb.debian.org/pkg{i}.deb";
            var (file, _) = CreateFile($"deb{i}.deb", url, $"payload-{url}");
            files.Add(file);
        }

        parts.Add(new InputPart { Id = "test-part", Title = "Test Part", Files = files });

        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = parts
        };

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        await engine.DownloadPartsAsync(ct: CancellationToken.None);

        Assert.True(maxOverallObserved <= 4, $"Observed overall concurrency: {maxOverallObserved} > 4");
        Assert.True(maxSnapshotObserved <= 2, $"Observed snapshot concurrency: {maxSnapshotObserved} > 2");
    }

    [Fact]
    public async Task Snapshots_MoveThroughWaitingCheckingDownloadingAndDone()
    {
        var (file1, payload1) = CreateFile("f1.deb", "https://deb.debian.org/f1.deb", "content1");
        var (file2, payload2) = CreateFile("f2.deb", "https://deb.debian.org/f2.deb", "content2");

        var part = new InputPart
        {
            Id = "multi-file",
            Title = "Multi File Part",
            Files = [file1, file2]
        };

        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [part]
        };

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            var bytes = req.RequestUri!.AbsoluteUri.Contains("f1") ? payload1 : payload2;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        });

        var snapshotsRecorded = new List<PartDownloadSnapshot>();
        var progress = new Progress<PartDownloadSnapshot>(s =>
        {
            lock (snapshotsRecorded) snapshotsRecorded.Add(s);
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        await engine.DownloadPartsAsync(progress: progress);

        Assert.Contains(snapshotsRecorded, s => s.State == PartDownloadState.Waiting);
        Assert.Contains(snapshotsRecorded, s => s.State == PartDownloadState.Checking);
        Assert.Contains(snapshotsRecorded, s => s.State == PartDownloadState.Downloading);
        Assert.Contains(snapshotsRecorded, s => s.State == PartDownloadState.Done);

        var last = snapshotsRecorded.Last(s => s.PartId == "multi-file");
        Assert.Equal(PartDownloadState.Done, last.State);
        Assert.Equal(2, last.FileCount);
        Assert.Equal(payload1.Length + payload2.Length, last.Total);
    }

    [Fact]
    public async Task Covers_AE6_AfterThreePartsFinishAndNetworkFails_NextRunReportsAlreadyPresentZeroRequests()
    {
        var (file1, p1) = CreateFile("p1.deb", "https://deb.debian.org/p1.deb", "p1");
        var (file2, p2) = CreateFile("p2.deb", "https://deb.debian.org/p2.deb", "p2");
        var (file3, p3) = CreateFile("p3.deb", "https://deb.debian.org/p3.deb", "p3");
        var (file4, p4) = CreateFile("p4.deb", "https://deb.debian.org/p4.deb", "p4");

        var parts = new List<InputPart>
        {
            new() { Id = "part1", Title = "Part 1", Files = [file1] },
            new() { Id = "part2", Title = "Part 2", Files = [file2] },
            new() { Id = "part3", Title = "Part 3", Files = [file3] },
            new() { Id = "part4", Title = "Part 4", Files = [file4] },
        };

        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = parts
        };

        var networkFailed = false;
        var requestCount = 0;

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            Interlocked.Increment(ref requestCount);
            var uri = req.RequestUri!.AbsoluteUri;
            if (networkFailed && uri.Contains("p4"))
            {
                throw new HttpRequestException("Simulated connection dropped");
            }

            byte[] b = uri.Contains("p1") ? p1 : uri.Contains("p2") ? p2 : uri.Contains("p3") ? p3 : p4;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) });
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);

        // Pre-download parts 1, 2, 3
        await engine.DownloadPartsAsync(["part1", "part2", "part3"]);

        // Now run with part4 failing due to network failure
        networkFailed = true;
        var ex = await Assert.ThrowsAsync<DownloadException>(() => engine.DownloadPartsAsync(["part4"]));
        Assert.Equal(FailureCode.NetworkUnavailable, ex.Code);

        // Next run: requests for parts 1, 2, 3 must be ZERO
        var requestsBefore = requestCount;
        networkFailed = false; // network restored

        var snapshots = new List<PartDownloadSnapshot>();
        var progress = new SyncProgress<PartDownloadSnapshot>(s =>
        {
            lock (snapshots) snapshots.Add(s);
        });
        await engine.DownloadPartsAsync(progress: progress);

        // Only part4 should have been fetched! Parts 1, 2, 3 were already present
        var requestsMadeInSecondRun = requestCount - requestsBefore;
        Assert.Equal(1, requestsMadeInSecondRun);

        var snap1 = snapshots.Last(s => s.PartId == "part1");
        var snap2 = snapshots.Last(s => s.PartId == "part2");
        var snap3 = snapshots.Last(s => s.PartId == "part3");

        Assert.True(snap1.AlreadyPresent);
        Assert.True(snap2.AlreadyPresent);
        Assert.True(snap3.AlreadyPresent);
    }

    [Fact]
    public async Task Covers_AE7_WithEveryFileCached_StepCompletesAndSendsNoRequests()
    {
        var (file1, p1) = CreateFile("f1.deb", "https://deb.debian.org/f1.deb", "f1");
        var (file2, p2) = CreateFile("f2.deb", "https://deb.debian.org/f2.deb", "f2");

        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [new InputPart { Id = "cached-part", Title = "Cached Part", Files = [file1, file2] }]
        };

        // Populate cache beforehand
        File.WriteAllBytes(_cache.GetPath(file1.Key), p1);
        File.WriteAllBytes(_cache.GetPath(file2.Key), p2);

        var requestsMade = 0;
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestsMade);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        await engine.DownloadPartsAsync();

        Assert.Equal(0, requestsMade);
    }

    [Fact]
    public async Task Covers_AE8_CachedFileWithChangedBytes_IsDetectedAndFetchedOnceMore()
    {
        var (file, validPayload) = CreateFile("tampered.deb", "https://deb.debian.org/tampered.deb", "valid-content");
        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [new InputPart { Id = "tampered-part", Title = "Tampered Part", Files = [file] }]
        };

        // Write corrupt bytes to cache
        File.WriteAllBytes(_cache.GetPath(file.Key), Encoding.UTF8.GetBytes("corrupted-content"));

        var requestsMade = 0;
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestsMade);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validPayload) });
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        await engine.DownloadPartsAsync();

        Assert.Equal(1, requestsMade);
        Assert.Equal(validPayload, await File.ReadAllBytesAsync(_cache.GetPath(file.Key)));
    }

    [Fact]
    public async Task SecondChecksumMismatch_EndsWithChecksumMismatchCodeAndLeavesNoFile()
    {
        var (file, _) = CreateFile("bad-hash.deb", "https://deb.debian.org/bad-hash.deb", "expected-content-abc");
        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [new InputPart { Id = "bad-part", Title = "Bad Part", Files = [file] }]
        };

        var requests = 0;
        var badPayload = Encoding.UTF8.GetBytes("expected-content-xyz"); // exact same length 20, different hash
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(badPayload) });
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        var ex = await Assert.ThrowsAsync<DownloadException>(() => engine.DownloadPartsAsync());

        Assert.Equal(FailureCode.ChecksumMismatch, ex.Code);
        Assert.Contains("Checksum mismatch", ex.Message);
        Assert.Contains(file.Key, ex.Message);
        Assert.Equal(2, requests); // Downloaded once, retried once, then failed closed
        Assert.False(File.Exists(_cache.GetPath(file.Key)));
        Assert.False(File.Exists(_cache.GetPartPath(file.Key)));
    }

    [Fact]
    public async Task SizeMismatch_ReportsWrongSizeAndDistinguishesFromChecksumMismatch()
    {
        var (file, _) = CreateFile("size-mismatch.deb", "https://deb.debian.org/size-mismatch.deb", "expected-content-long");
        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [new InputPart { Id = "size-part", Title = "Size Part", Files = [file] }]
        };

        var requests = 0;
        var shortPayload = Encoding.UTF8.GetBytes("short");
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(shortPayload) });
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        var ex = await Assert.ThrowsAsync<DownloadException>(() => engine.DownloadPartsAsync());

        Assert.Equal(FailureCode.ChecksumMismatch, ex.Code);
        Assert.Contains("Wrong size: expected", ex.Message);
        Assert.Contains($"expected {file.Size} bytes, got {shortPayload.Length}", ex.Message);
        Assert.Contains(file.Key, ex.Message);
        Assert.Contains("size-part", ex.Message);
        Assert.Equal(2, requests);
        Assert.False(File.Exists(_cache.GetPath(file.Key)));
        Assert.False(File.Exists(_cache.GetPartPath(file.Key)));
    }

    [Fact]
    public async Task FirstAddress404_FallsBackToSecondAddress()
    {
        var payload = Encoding.UTF8.GetBytes("good-download");
        var sha = Convert.ToHexString(SHA256.HashData(payload));

        var file = new InputFile
        {
            Key = "fallback.deb",
            Path = "guest/fallback.deb",
            Size = payload.Length,
            Sha256 = sha,
            Urls = ["https://deb.debian.org/fallback.deb", "https://snapshot.debian.org/fallback.deb"],
            InGuest = true
        };

        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [new InputPart { Id = "fallback-part", Title = "Fallback Part", Files = [file] }]
        };

        var handler = new MockHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.Host == "deb.debian.org")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        await engine.DownloadPartsAsync();

        Assert.True(File.Exists(_cache.GetPath(file.Key)));
        Assert.Equal(payload, await File.ReadAllBytesAsync(_cache.GetPath(file.Key)));
    }

    [Fact]
    public async Task AllAddressesFailing_EndsWithFailureNamingPart()
    {
        var file = new InputFile
        {
            Key = "unreachable.deb",
            Path = "guest/unreachable.deb",
            Size = 100,
            Sha256 = new string('0', 64),
            Urls = ["https://deb.debian.org/unreachable.deb", "https://snapshot.debian.org/unreachable.deb"],
            InGuest = true
        };

        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [new InputPart { Id = "system-tools", Title = "System tools", Files = [file] }]
        };

        var handler = new MockHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        var ex = await Assert.ThrowsAsync<DownloadException>(() => engine.DownloadPartsAsync());

        Assert.Equal("system-tools", ex.PartId);
        Assert.Contains("system-tools", ex.Message);
    }

    [Fact]
    public async Task RefusalFromLimitedHost_RetriesWithBackoffAndSucceeds()
    {
        var (file, payload) = CreateFile("retry.deb", "https://snapshot.debian.org/retry.deb", "retry-success");
        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [new InputPart { Id = "retry-part", Title = "Retry Part", Files = [file] }]
        };

        var attempts = 0;
        var delays = new List<TimeSpan>();

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (ts, _) =>
        {
            delays.Add(ts);
            return Task.CompletedTask;
        });

        await engine.DownloadPartsAsync();

        Assert.Equal(2, attempts);
        Assert.Single(delays);
        Assert.Equal(payload, await File.ReadAllBytesAsync(_cache.GetPath(file.Key)));
    }

    [Fact]
    public async Task Cancellation_RemovesPartFilesAndPreservesFinishedFiles()
    {
        var (file1, p1) = CreateFile("finished-before-cancel.deb", "https://deb.debian.org/f1.deb", "f1");
        File.WriteAllBytes(_cache.GetPath(file1.Key), p1);

        var (file2, _) = CreateFile("cancelling.deb", "https://deb.debian.org/f2.deb", "f2");

        var inputs = new BuildInputs
        {
            SchemaVersion = 1,
            Versions = new ApplianceVersions(),
            Parts = [new InputPart { Id = "part-cancel", Title = "Cancel Part", Files = [file1, file2] }]
        };

        using var cts = new CancellationTokenSource();
        var handler = new MockHttpMessageHandler((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var engine = new DownloadEngine(inputs, _cache, handler, delay: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadPartsAsync(ct: cts.Token));

        Assert.True(File.Exists(_cache.GetPath(file1.Key)));
        Assert.False(File.Exists(_cache.GetPartPath(file2.Key)));
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this((req, _) => handler(req)) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
