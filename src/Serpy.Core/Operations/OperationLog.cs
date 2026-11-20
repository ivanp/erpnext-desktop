using System.Text;

namespace Serpy.Core.Operations;

/// <summary>
/// Durable per-operation text log (KTD3). One line per event, flushed on every write and opened
/// with shared read and write access, so Show details can tail the file while an operation still
/// runs and QEMU's own output events can append to the same file. Callers write stage changes,
/// QEMU start and exit, guest console lines and full exception text. They never write passwords,
/// session cookies, request bodies or key material.
/// </summary>
public sealed class OperationLog : IDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _secrets = [];
    private readonly StreamWriter _writer;
    private readonly Func<DateTimeOffset> _clock;
    private bool _disposed;

    public string Path { get; }

    public OperationLog(string path, Func<DateTimeOffset>? clock = null)
    {
        Path = path;
        _clock = clock ?? (() => DateTimeOffset.Now);
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>
    /// Registers a secret (the Administrator password) that must never reach the file. Every later
    /// write masks it, whatever text carries it: a stage message, a guest line or an exception.
    /// </summary>
    public void Redact(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (_gate) _secrets.Add(secret);
    }

    public void Info(string message) => Write("info", message);

    public void Stage(string stage, string message) => Write(stage, message);

    /// <summary>One line per guest console line, so the log shows what the guest printed.</summary>
    public void Guest(string line) => Write("guest", line);

    /// <summary>Full exception text including inner exceptions.</summary>
    public void Error(string message, Exception ex) => Write("error", message + "\n" + ex);

    private void Write(string kind, string message)
    {
        var stamp = _clock().ToString("HH:mm:ss");
        lock (_gate)
        {
            // QEMU output events can still fire after the operation that owns the log has ended.
            if (_disposed) return;
            foreach (var secret in _secrets)
                message = message.Replace(secret, "********", StringComparison.Ordinal);
            foreach (var line in message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                _writer.WriteLine($"{stamp}  {kind,-10} {line}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
