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

    /// <summary>
    /// Upper bound on resumed attempts per file. Each attempt that receives at least one new byte
    /// (forward progress) does not consume the retry budget, so a server that cuts the connection
    /// after a fixed number of bytes can still finish a large file by resuming; this cap prevents an
    /// infinite loop if a server keeps returning tiny or empty 206 responses forever.
    /// </summary>
    private const int MaxResumedAttemptsPerFile = 50;
    /// <summary>
    /// Default inactivity timeout in seconds during stream body read. If no bytes arrive within this duration,
    /// the read is aborted as stalled so the engine can retry or fall back to an alternate URL.
    /// In clean-install VM environments, stalled TCP connections to wheel/package hosts otherwise
    /// hang indefinitely because HttpClient.Timeout only covers the header phase under ResponseHeadersRead.
    /// </summary>
    public const int DefaultStallTimeoutSeconds = 60;

    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(DefaultStallTimeoutSeconds);

    private readonly BuildInputs _inputs;
    private readonly DownloadCache _cache;
    private readonly HttpMessageHandler? _httpHandler;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _stallTimeout;
    private readonly SemaphoreSlim _overallThrottle = new(MaxOverallConcurrentDownloads, MaxOverallConcurrentDownloads);
    private readonly SemaphoreSlim _snapshotThrottle = new(MaxSnapshotConcurrentDownloads, MaxSnapshotConcurrentDownloads);

    private readonly ConcurrentDictionary<string, PartDownloadSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public DownloadCache Cache => _cache;
    public BuildInputs Inputs => _inputs;

    public TimeSpan StallTimeout => _stallTimeout;

    public DownloadEngine(
        BuildInputs inputs,
        DownloadCache cache,
        HttpMessageHandler? httpHandler = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? stallTimeout = null)
    {
        _inputs = inputs;
        _cache = cache;
        _httpHandler = httpHandler;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
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
                            var currentBytes = partBytesMap.AddOrUpdate(part.Id, Math.Max(0, bytesDelta), (_, old) => Math.Max(0, old + bytesDelta));
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
        DeletePart(partPath);

        var urls = file.Urls ?? [];
        if (urls.Count == 0)
            throw new DownloadException(FailureCode.Unknown, $"File '{file.Key}' in part '{part.Id}' has no download URLs.", part.Id);

        var fileBytesReceived = 0L;
        void ReportBytes(long delta)
        {
            fileBytesReceived += delta;
            onBytesReceived(delta);
        }
        void ResetBytes()
        {
            if (fileBytesReceived > 0)
            {
                onBytesReceived(-fileBytesReceived);
                fileBytesReceived = 0;
            }
        }

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
                    await DownloadFromUrlWithRetryAsync(url, partPath, file, ReportBytes, ct).ConfigureAwait(false);
                    anyAddressSucceeded = true;
                    break;
                }
                catch (HttpResponseException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // 404 -> fallback to next address; a resumed .part belongs to this URL only.
                    lastError = ex;
                    ResetBytes();
                    DeletePart(partPath);
                    continue;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsDiskFullException(ex))
                {
                    // Disk full: do not retry other URLs, fail immediately
                    lastError = ex;
                    ResetBytes();
                    DeletePart(partPath);
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (IsNetworkException(ex) || ex is HttpResponseException))
                {
                    // Fallback to next URL: a resumed .part belongs to the previous URL's byte
                    // sequence, so it must never be appended to another server's stream.
                    lastError = ex;
                    ResetBytes();
                    DeletePart(partPath);
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
                // A corrupt (possibly resumed) file must be refetched from scratch.
                ResetBytes();
                DeletePart(partPath);
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

    private static void DeletePart(string partPath)
    {
        if (File.Exists(partPath))
        {
            try { File.Delete(partPath); } catch { }
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
        var noProgressCount = 0;

        while (true)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();

            if (attempt > MaxResumedAttemptsPerFile)
            {
                throw new HttpRequestException(
                    $"Download of {url} reached maximum resumed attempts ({MaxResumedAttemptsPerFile}) without completing.");
            }

            await _overallThrottle.WaitAsync(ct).ConfigureAwait(false);
            var snapshotAcquired = false;
            if (isSnapshot)
            {
                await _snapshotThrottle.WaitAsync(ct).ConfigureAwait(false);
                snapshotAcquired = true;
            }

            var resumeFrom = 0L;
            if (File.Exists(partPath))
            {
                var partLength = new FileInfo(partPath).Length;
                if (partLength > 0 && (file.Size <= 0 || partLength < file.Size))
                {
                    resumeFrom = partLength;
                }
            }

            var bytesBefore = resumeFrom;

            try
            {
                await StreamToFileAsync(url, partPath, file, onBytesReceived, resumeFrom, ct).ConfigureAwait(false);
                return;
            }
            catch (HttpResponseException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Do not retry 404 on the same URL; let outer loop fallback to next URL
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpResponseException httpEx && IsRefusal(httpEx.StatusCode) || ex is TaskCanceledException or TimeoutException or HttpRequestException))
            {
                var partLengthNow = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                if (partLengthNow <= bytesBefore)
                {
                    noProgressCount++;
                }

                if (noProgressCount > maxRetries)
                {
                    throw;
                }

                var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, Math.Min(noProgressCount, maxRetries) - 1));
                await _delay(backoff, ct).ConfigureAwait(false);
            }
            finally
            {
                if (snapshotAcquired) _snapshotThrottle.Release();
                _overallThrottle.Release();
            }

        }
    }

    private async Task<long> StreamToFileAsync(
        string url,
        string partPath,
        InputFile file,
        Action<long> onBytesReceived,
        long resumeFrom,
        CancellationToken ct)
    {
        using var client = _httpHandler is null ? new HttpClient() : new HttpClient(_httpHandler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout downloading {url}");
        }

        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound, $"404 Not Found: {url}");
            }
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (resumeFrom > 0)
                {
                    onBytesReceived(-resumeFrom);
                }
                if (File.Exists(partPath))
                {
                    try { File.Delete(partPath); } catch { }
                }
                throw new HttpRequestException($"HTTP 416 RequestedRangeNotSatisfiable from {url} resuming from {resumeFrom}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpResponseException(response.StatusCode, $"HTTP {(int)response.StatusCode} {response.StatusCode} from {url}");
            }

            // Resume policy (see plan defect 7):
            // - resumeFrom > 0 and a 206 whose Content-Range starts exactly at the requested offset
            //   (and whose total matches file.Size when both are known): append to the .part file.
            // - a 200 to a Range request (server ignores Range): truncate and restart from 0.
            // - a 206 with a wrong/missing Content-Range start: reject the resume; delete the .part
            //   and throw HttpRequestException so the next attempt starts fresh from 0.
            var append = false;
            if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange is null
                    || contentRange.From != resumeFrom
                    || (contentRange.Length.HasValue && file.Size > 0 && contentRange.Length.Value != file.Size))
                {
                    onBytesReceived(-resumeFrom);
                    if (File.Exists(partPath))
                    {
                        try { File.Delete(partPath); } catch { }
                    }
                    throw new HttpRequestException(
                        $"Unacceptable Content-Range from {url} (expected start {resumeFrom})");
                }
                append = true;
            }
            else if (resumeFrom > 0)
            {
                // Server ignored Range and answered 200 (or an unexpected success status): restart from 0.
                onBytesReceived(-resumeFrom);
                resumeFrom = 0;
            }

            if (!append && File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
            }

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (!ct.IsCancellationRequested && !IsDiskFullException(ex))
            {
                throw new HttpRequestException($"Network stream open error from {url}: {ex.Message}", ex);
            }

            await using var streamRegistration = stream.ConfigureAwait(false);
            await using var fileStream = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

            var buffer = new byte[65536];
            long received = 0;
            while (true)
            {
                int read;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    readCts.CancelAfter(_stallTimeout);
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No data received for {_stallTimeout.TotalSeconds}s from {url}");
                    }
                    catch (IOException ex) when (!ct.IsCancellationRequested && !IsDiskFullException(ex))
                    {
                        throw new HttpRequestException($"Network stream read error from {url}: {ex.Message}", ex);
                    }
                }

                if (read == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                onBytesReceived(read);
                received += read;
            }

            return received;
        }
        catch (IOException ex) when (IsDiskFullException(ex))
        {
            if (File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
            }
            throw;
        }
        catch (HttpResponseException)
        {
            // A rejected resume (416 / bad Content-Range) must not leave a stale .part behind:
            // the next attempt starts from 0, and any previously reported bytes are undone.
            if (resumeFrom > 0)
            {
                onBytesReceived(-resumeFrom);
            }
            if (File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
            }
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            // Transient network failure during read/open: KEEP .part on disk so the next attempt can resume!
            throw;
        }
        catch
        {
            // Non-network failure or cancellation: discard partial file.
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
        return ex is HttpRequestException or System.Net.Sockets.SocketException or WebException or TimeoutException;
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
