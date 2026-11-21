using System.Formats.Tar;
using System.Security.Cryptography;
using Serpy.Core.Contracts;
using Serpy.Core.Downloads;
using Serpy.Core.Versions;

namespace Serpy.Core.Images;

/// <summary>
/// Thrown when a cached input fails checksum re-verification while packing the transport disk (KTD29).
/// </summary>
public sealed class CacheTransportChecksumMismatchException : Exception
{
    public FailureCode Code => FailureCode.ChecksumMismatch;
    public string Key { get; }
    public string ExpectedChecksum { get; }
    public string ActualChecksum { get; }

    public CacheTransportChecksumMismatchException(
        string key,
        string expectedChecksum,
        string actualChecksum,
        string message)
        : base(message)
    {
        Key = key;
        ExpectedChecksum = expectedChecksum;
        ActualChecksum = actualChecksum;
    }
}

/// <summary>
/// Packs the offline cache into an uncompressed tar archive on a read-only transport disk (KTD29).
/// Re-verifies every file's checksum while streaming; any tampered or corrupted file aborts the build
/// with ChecksumMismatch before VM boot.
/// </summary>
public sealed class CacheTransport : IDisposable
{
    public string FilePath { get; }

    public CacheTransport(string filePath)
    {
        FilePath = filePath;
    }

    public void Delete() => Delete(FilePath);

    public void Dispose() => Delete();

    public static void Delete(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Writes the guest cache files from <paramref name="inputs"/> and <paramref name="cache"/>
    /// into <paramref name="destinationTarPath"/> in PAX tar format.
    /// Returns the file path of the written tar.
    /// </summary>
    public static string WriteTar(
        string destinationTarPath,
        BuildInputs inputs,
        DownloadCache cache,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTarPath);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(cache);

        var destinationDir = Path.GetDirectoryName(Path.GetFullPath(destinationTarPath));
        if (!string.IsNullOrEmpty(destinationDir))
            Directory.CreateDirectory(destinationDir);

        if (File.Exists(destinationTarPath))
            File.Delete(destinationTarPath);

        var guestFiles = (inputs.Parts ?? Enumerable.Empty<InputPart>())
            .SelectMany(p => p.Files ?? Enumerable.Empty<InputFile>())
            .Where(f => f.InGuest && !string.IsNullOrWhiteSpace(f.Path))
            .ToList();

        try
        {
            using (var tarFileStream = new FileStream(destinationTarPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                using (var tarWriter = new TarWriter(tarFileStream, TarEntryFormat.Pax, leaveOpen: true))
                {
                    foreach (var file in guestFiles)
                    {
                        ct.ThrowIfCancellationRequested();

                        var sourcePath = cache.GetPath(file.Key);
                        if (!File.Exists(sourcePath))
                        {
                            throw new CacheTransportChecksumMismatchException(
                                file.Key,
                                file.Sha256 ?? file.Sha512 ?? "known-hash",
                                "missing",
                                $"Cached file '{file.Key}' at '{sourcePath}' does not exist on disk.");
                        }

                        var entryPath = file.Path.Replace('\\', '/').TrimStart('/');

                        IncrementalHash? hasher = null;
                        string? expectedHash = null;
                        if (!string.IsNullOrWhiteSpace(file.Sha256))
                        {
                            hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            expectedHash = file.Sha256;
                        }
                        else if (!string.IsNullOrWhiteSpace(file.Sha512))
                        {
                            hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                            expectedHash = file.Sha512;
                        }

                        using (hasher)
                        using (var sourceStream = File.OpenRead(sourcePath))
                        {
                            if (file.Size > 0 && sourceStream.Length != file.Size)
                            {
                                throw new CacheTransportChecksumMismatchException(
                                    file.Key,
                                    expectedHash ?? file.Size.ToString(),
                                    sourceStream.Length.ToString(),
                                    $"Size mismatch for cached file '{file.Key}'. Expected {file.Size} bytes, got {sourceStream.Length} bytes.");
                            }

                            using var hashingStream = new HashingReadStream(sourceStream, hasher);
                            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryPath)
                            {
                                DataStream = hashingStream,
                                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                            };

                            tarWriter.WriteEntry(entry);

                            if (hasher is not null && expectedHash is not null)
                            {
                                var actualHash = Convert.ToHexString(hasher.GetHashAndReset());
                                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new CacheTransportChecksumMismatchException(
                                        file.Key,
                                        expectedHash,
                                        actualHash,
                                        $"Checksum mismatch for cached file '{file.Key}' (path '{file.Path}'). Expected {expectedHash}, got {actualHash}.");
                                }
                            }
                        }
                    }
                }

                // Ensure file size is a multiple of 512 bytes per tar specification
                if (tarFileStream.Length % 512 != 0)
                {
                    var remainder = (int)(tarFileStream.Length % 512);
                    var pad = 512 - remainder;
                    tarFileStream.Write(new byte[pad]);
                }
            }

            return destinationTarPath;
        }
        catch
        {
            Delete(destinationTarPath);
            throw;
        }
    }

    /// <summary>
    /// Writes the guest cache files into <paramref name="destinationTarPath"/> and returns
    /// a disposable <see cref="CacheTransport"/> instance.
    /// </summary>
    public static CacheTransport Write(
        string destinationTarPath,
        BuildInputs inputs,
        DownloadCache cache,
        CancellationToken ct = default)
    {
        var path = WriteTar(destinationTarPath, inputs, cache, ct);
        return new CacheTransport(path);
    }

    private sealed class HashingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash? _hasher;

        public HashingReadStream(Stream inner, IncrementalHash? hasher)
        {
            _inner = inner;
            _hasher = hasher;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (read > 0 && _hasher is not null)
            {
                _hasher.AppendData(buffer, offset, read);
            }
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            if (read > 0 && _hasher is not null)
            {
                _hasher.AppendData(buffer[..read]);
            }
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => _inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
