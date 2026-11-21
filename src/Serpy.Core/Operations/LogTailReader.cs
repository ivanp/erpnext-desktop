using System.Text;

namespace Serpy.Core.Operations;

/// <summary>
/// Follows one operation log while it is still being written (KTD3). Each <see cref="ReadNew"/>
/// returns the complete lines that arrived since the last call, in order. The log is opened with
/// shared read and write access by <see cref="OperationLog"/>, so this reader never blocks a writer
/// and a writer never blocks it. It polls: the caller owns the timer, so no thread is held here and
/// a file that does not exist yet needs no watcher.
///
/// A line is returned only once its newline has arrived. The reader keeps the bytes of a half-written
/// line, not its text: a newline byte never occurs inside a multi-byte UTF-8 character, so cutting at
/// the last newline byte can never split a character, and the rest is decoded when it is complete.
/// </summary>
public sealed class LogTailReader(string path)
{
    private const byte LineFeed = (byte)'\n';

    private long _offset;
    private byte[] _pending = [];

    public string Path { get; } = path;

    /// <summary>Lines that have been returned so far.</summary>
    public long LinesRead { get; private set; }

    /// <summary>
    /// The complete lines written since the last call. Empty when the file does not exist yet, when
    /// it cannot be read right now, or when nothing new arrived. A file that became shorter than the
    /// reader's position was replaced, so reading starts again from its first byte.
    /// </summary>
    public IReadOnlyList<string> ReadNew()
    {
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < _offset)
            {
                _offset = 0;
                _pending = [];
            }

            if (stream.Length == _offset) return [];

            stream.Seek(_offset, SeekOrigin.Begin);
            var data = new byte[_pending.Length + (stream.Length - _offset)];
            _pending.CopyTo(data, 0);
            var read = 0;
            while (_pending.Length + read < data.Length)
            {
                var n = stream.Read(data, _pending.Length + read, data.Length - _pending.Length - read);
                if (n == 0) break;
                read += n;
            }

            _offset += read;
            var total = _pending.Length + read;
            if (total == 0) return []; // the file shrank between the length check and the read
            var lastBreak = Array.LastIndexOf(data, LineFeed, total - 1);
            if (lastBreak < 0)
            {
                _pending = data[..total];
                return [];
            }

            _pending = data[(lastBreak + 1)..total];
            var lines = Encoding.UTF8.GetString(data, 0, lastBreak)
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .ToList();
            LinesRead += lines.Count;
            return lines;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (IOException) { return []; } // locked for this instant; the next poll tries again
        catch (UnauthorizedAccessException) { return []; }
    }
}
