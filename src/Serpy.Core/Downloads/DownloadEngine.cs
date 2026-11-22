using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Serpy.Core.Contracts;
using Serpy.Core.Versions;

namespace Serpy.Core.Downloads;

public enum PartDownloadState
{
    Waiting,
    Checking,
    Downloading,
    Done,
    Failed
}

public sealed record PartDownloadSnapshot(
    string PartId,
    PartDownloadState State,
    long Bytes,
    long Total,
    int FileCount,
    bool AlreadyPresent);

public sealed class DownloadException(FailureCode code, string message, string? partId = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public FailureCode Code { get; } = code;
    public string? PartId { get; } = partId;
}

public sealed class DownloadEngine
{
    private const int MaxOverallConcurrentDownloads = 4;
    private const int MaxSnapshotConcurrentDownloads = 2;
    private const string SnapshotHost = "snapshot.debian.org";

    private readonly BuildInputs _inputs;
    private readonly DownloadCache _cache;
    private readonly HttpMessageHandler? _httpHandler;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly SemaphoreSlim _overallThrottle = new(MaxOverallConcurrentDownloads, MaxOverallConcurrentDownloads);
    private readonly SemaphoreSlim _snapshotThrottle = new(MaxSnapshotConcurrentDownloads, MaxSnapshotConcurrentDownloads);

    private readonly ConcurrentDictionary<string, PartDownloadSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public DownloadCache Cache => _cache;
    public BuildInputs Inputs => _inputs;

    public DownloadEngine(
        BuildInputs inputs,
        DownloadCache cache,
        HttpMessageHandler? httpHandler = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _inputs = inputs;
        _cache = cache;
        _httpHandler = httpHandler;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public IReadOnlyList<PartDownloadSnapshot> GetSnapshots() =>
        _inputs.Parts
            .Select(p => _snapshots.GetValueOrDefault(p.Id, new PartDownloadSnapshot(p.Id, PartDownloadState.Waiting, 0, p.Files.Sum(f => f.Size), p.Files.Count, false)))
            .ToList();

    public async Task DownloadPartsAsync(
        IReadOnlyList<string>? partIds = null,
        IProgress<PartDownloadSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        _cache.DeleteLeftovers();

        var partsToProcess = _inputs.Parts;
        if (partIds is { Count: > 0 })
        {
            var filter = new HashSet<string>(partIds, StringComparer.OrdinalIgnoreCase);
            partsToProcess = partsToProcess.Where(p => filter.Contains(p.Id)).ToList();
        }

        // Initialize snapshots for requested parts
        foreach (var part in partsToProcess)
        {
            var total = part.Files.Sum(f => f.Size);
            var fileCount = part.Files.Count;
            var initial = new PartDownloadSnapshot(part.Id, PartDownloadState.Waiting, 0, total, fileCount, false);
            _snapshots[part.Id] = initial;
            progress?.Report(initial);
        }

        // Step 1: Check cache for each part
        var filesToDownloadByPart = new Dictionary<InputPart, List<InputFile>>();
        var partProgressLock = new object();
        var partBytesMap = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in partsToProcess)
        {
            var total = part.Files.Sum(f => f.Size);
            var fileCount = part.Files.Count;
            var checking = new PartDownloadSnapshot(part.Id, PartDownloadState.Checking, 0, total, fileCount, false);
            _snapshots[part.Id] = checking;
            progress?.Report(checking);

            var needed = new List<InputFile>();
            long verifiedBytes = 0;

            foreach (var file in part.Files)
            {
                if (_cache.TryVerify(file))
                {
                    verifiedBytes += file.Size;
                }
                else
                {
                    needed.Add(file);
                }
            }

            partBytesMap[part.Id] = verifiedBytes;

            if (needed.Count == 0)
            {
                var done = new PartDownloadSnapshot(part.Id, PartDownloadState.Done, total, total, fileCount, AlreadyPresent: true);
                _snapshots[part.Id] = done;
                progress?.Report(done);
            }
            else
            {
                filesToDownloadByPart[part] = needed;
                var waiting = new PartDownloadSnapshot(part.Id, PartDownloadState.Waiting, verifiedBytes, total, fileCount, AlreadyPresent: false);
                _snapshots[part.Id] = waiting;
                progress?.Report(waiting);
            }
        }

        if (filesToDownloadByPart.Count == 0)
        {
            // Everything is already cached!
            return;
        }

        // Step 2: Download needed files concurrently across parts
        var downloadTasks = new List<Task>();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        foreach (var (part, files) in filesToDownloadByPart)
        {
            var downloading = new PartDownloadSnapshot(part.Id, PartDownloadState.Downloading, partBytesMap[part.Id], part.Files.Sum(f => f.Size), part.Files.Count, false);
            _snapshots[part.Id] = downloading;
            progress?.Report(downloading);

            var partFilesRemaining = new ConcurrentDictionary<string, byte>(files.ToDictionary(f => f.Key, _ => (byte)0, StringComparer.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                downloadTasks.Add(Task.Run(async () =>
                {
                    await DownloadSingleFileAsync(part, file, (bytesDelta) =>
                    {
                        lock (partProgressLock)
                        {
                            var currentBytes = partBytesMap.AddOrUpdate(part.Id, bytesDelta, (_, old) => old + bytesDelta);
                            var snap = new PartDownloadSnapshot(part.Id, PartDownloadState.Downloading, currentBytes, part.Files.Sum(f => f.Size), part.Files.Count, false);
                            _snapshots[part.Id] = snap;
                            progress?.Report(snap);
                        }
                    }, linkedCts.Token).ConfigureAwait(false);

                    partFilesRemaining.TryRemove(file.Key, out _);
                    if (partFilesRemaining.IsEmpty)
                    {
                        lock (partProgressLock)
                        {
                            var total = part.Files.Sum(f => f.Size);
                            var done = new PartDownloadSnapshot(part.Id, PartDownloadState.Done, total, total, part.Files.Count, false);
                            _snapshots[part.Id] = done;
                            progress?.Report(done);
                        }
                    }
                }, linkedCts.Token));
            }
        }

        try
        {
            await Task.WhenAll(downloadTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cache.DeleteLeftovers();
            if (ct.IsCancellationRequested)
                throw;
            throw new DownloadException(FailureCode.Unknown, "Download operation was cancelled.");
        }
        catch (Exception ex)
        {
            _cache.DeleteLeftovers();

            // Extract innermost DownloadException if present
            var dlEx = ex as DownloadException ?? (ex as AggregateException)?.Flatten().InnerExceptions.OfType<DownloadException>().FirstOrDefault();
            if (dlEx is not null)
            {
                if (dlEx.PartId is not null && _snapshots.TryGetValue(dlEx.PartId, out var snap))
                {
                    var failed = snap with { State = PartDownloadState.Failed };
                    _snapshots[dlEx.PartId] = failed;
                    progress?.Report(failed);
                }
                throw dlEx;
            }

            var netEx = (ex as AggregateException)?.Flatten().InnerExceptions.FirstOrDefault(e => IsNetworkException(e))
                ?? (IsNetworkException(ex) ? ex : null);
            if (netEx is not null)
            {
                throw new DownloadException(FailureCode.NetworkUnavailable, $"Network unavailable: {netEx.Message}", innerException: netEx);
            }

            var diskEx = (ex as AggregateException)?.Flatten().InnerExceptions.FirstOrDefault(e => IsDiskFullException(e))
                ?? (IsDiskFullException(ex) ? ex : null);
            if (diskEx is not null)
            {
                throw new DownloadException(FailureCode.DiskFull, $"Disk full: {diskEx.Message}", innerException: diskEx);
            }

            throw;
        }
    }

    private async Task DownloadSingleFileAsync(
        InputPart part,
        InputFile file,
        Action<long> onBytesReceived,
        CancellationToken ct)
    {
        var targetPath = _cache.GetPath(file.Key);
        var partPath = _cache.GetPartPath(file.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var urls = file.Urls ?? [];
        if (urls.Count == 0)
            throw new DownloadException(FailureCode.Unknown, $"File '{file.Key}' in part '{part.Id}' has no download URLs.", part.Id);

        var mismatchCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Exception? lastError = null;
            var anyAddressSucceeded = false;

            for (var urlIndex = 0; urlIndex < urls.Count; urlIndex++)
            {
                var url = urls[urlIndex];
                try
                {
                    await DownloadFromUrlWithRetryAsync(url, partPath, file, onBytesReceived, ct).ConfigureAwait(false);
                    anyAddressSucceeded = true;
                    break;
                }
                catch (HttpResponseException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // 404 -> fallback to next address
                    lastError = ex;
                    continue;
                }
                catch (Exception ex) when (urlIndex + 1 < urls.Count && (IsNetworkException(ex) || ex is HttpResponseException))
                {
                    // Fallback to next URL
                    lastError = ex;
                    continue;
                }
            }

            if (!anyAddressSucceeded)
            {
                if (lastError is not null && IsDiskFullException(lastError))
                    throw new DownloadException(FailureCode.DiskFull, $"Disk full downloading part '{part.Id}': {lastError.Message}", part.Id, lastError);

                if (lastError is not null && IsNetworkException(lastError))
                    throw new DownloadException(FailureCode.NetworkUnavailable, $"Network unavailable downloading part '{part.Id}': {lastError.Message}", part.Id, lastError);

                throw new DownloadException(
                    FailureCode.NetworkUnavailable,
                    $"All addresses failed for file '{file.Key}' in part '{part.Id}'.",
                    part.Id,
                    lastError);
            }

            // Verify size and checksum on .part
            var (integrityValid, mismatchReason) = VerifyFileIntegrity(partPath, file);
            if (!integrityValid)
            {
                mismatchCount++;
                if (File.Exists(partPath))
                {
                    try { File.Delete(partPath); } catch { }
                }

                if (mismatchCount >= 2)
                {
                    if (File.Exists(targetPath))
                    {
                        try { File.Delete(targetPath); } catch { }
                    }
                    var failMsg = mismatchReason is not null && mismatchReason.StartsWith("Wrong size", StringComparison.OrdinalIgnoreCase)
                        ? $"Wrong size for file '{file.Key}' in part '{part.Id}': {mismatchReason}. Discarded bad file."
                        : $"Second checksum mismatch for file '{file.Key}' in part '{part.Id}': {mismatchReason ?? "hash mismatch"}. Discarded bad file.";
                    throw new DownloadException(
                        FailureCode.ChecksumMismatch,
                        failMsg,
                        part.Id);
                }

                // Refetch once
                continue;
            }

            // Move .part to final
            File.Move(partPath, targetPath, overwrite: true);
            _cache.RecordVerified(file.Key);
            return;
        }
    }

    private async Task DownloadFromUrlWithRetryAsync(
        string url,
        string partPath,
        InputFile file,
        Action<long> onBytesReceived,
        CancellationToken ct)
    {
        var uri = new Uri(url);
        var isSnapshot = uri.Host.Equals(SnapshotHost, StringComparison.OrdinalIgnoreCase);

        var maxRetries = 3;
        var attempt = 0;

        while (true)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();

            await _overallThrottle.WaitAsync(ct).ConfigureAwait(false);
            var snapshotAcquired = false;
            if (isSnapshot)
            {
                await _snapshotThrottle.WaitAsync(ct).ConfigureAwait(false);
                snapshotAcquired = true;
            }

            try
            {
                await StreamToFileAsync(url, partPath, onBytesReceived, ct).ConfigureAwait(false);
                return;
            }
            catch (HttpResponseException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Do not retry 404 on the same URL; let outer loop fallback to next URL
                throw;
            }
            catch (HttpResponseException ex) when (IsRefusal(ex.StatusCode) && attempt <= maxRetries)
            {
                // Exponential backoff
                var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                await _delay(backoff, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt <= maxRetries && (ex is TaskCanceledException or TimeoutException or HttpRequestException))
            {
                var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                await _delay(backoff, ct).ConfigureAwait(false);
            }
            finally
            {
                if (snapshotAcquired) _snapshotThrottle.Release();
                _overallThrottle.Release();
            }
        }
    }

    private async Task StreamToFileAsync(
        string url,
        string partPath,
        Action<long> onBytesReceived,
        CancellationToken ct)
    {
        using var client = _httpHandler is null ? new HttpClient() : new HttpClient(_httpHandler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout downloading {url}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HttpResponseException(HttpStatusCode.NotFound, $"404 Not Found: {url}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpResponseException(response.StatusCode, $"HTTP {(int)response.StatusCode} {response.StatusCode} from {url}");
        }

        try
        {
            if (File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                onBytesReceived(read);
            }
        }
        catch (IOException ex) when (IsDiskFullException(ex))
        {
            if (File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
            }
            throw;
        }
        catch
        {
            if (File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
            }
            throw;
        }
    }

    private static (bool Valid, string? Reason) VerifyFileIntegrity(string path, InputFile file)
    {
        if (!File.Exists(path)) return (false, "File does not exist");

        var info = new FileInfo(path);
        if (file.Size > 0 && info.Length != file.Size)
            return (false, $"Wrong size: expected {file.Size} bytes, got {info.Length}");

        if (!string.IsNullOrWhiteSpace(file.Sha256))
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(sha.ComputeHash(stream));
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return (false, $"Checksum mismatch: expected SHA-256 {file.Sha256}, got {actual}");
            return (true, null);
        }

        if (!string.IsNullOrWhiteSpace(file.Sha512))
        {
            using var sha = SHA512.Create();
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(sha.ComputeHash(stream));
            if (!string.Equals(actual, file.Sha512, StringComparison.OrdinalIgnoreCase))
                return (false, $"Checksum mismatch: expected SHA-512 {file.Sha512}, got {actual}");
            return (true, null);
        }

        return (false, "No checksum specified for file");
    }

    private static bool IsRefusal(HttpStatusCode code) =>
        code is (HttpStatusCode)429 or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout;

    private static bool IsNetworkException(Exception ex)
    {
        return ex is HttpRequestException or System.Net.Sockets.SocketException or WebException;
    }

    private static bool IsDiskFullException(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            // Win32 ERROR_DISK_FULL = 0x70 (112), ERROR_HANDLE_DISK_FULL = 0x27 (39)
            var hr = ioEx.HResult & 0xFFFF;
            if (hr is 112 or 39) return true;
            if (ioEx.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase) ||
                ioEx.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
                ioEx.Message.Contains("space", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private sealed class HttpResponseException(HttpStatusCode statusCode, string message) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
