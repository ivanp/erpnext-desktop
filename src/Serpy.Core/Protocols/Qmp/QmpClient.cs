using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Serpy.Core.Protocols.Json;

namespace Serpy.Core.Protocols.Qmp;

/// <summary>
/// QMP monitor client over its TCP chardev with mutual TLS (KTD3).
/// Protocol flow:
///   1. Connect → receive greeting line (JSON with QMP key).
///   2. Send qmp_capabilities → receive empty return.
///   3. Subscribe event reader in background.
///   4. Send commands with correlated request IDs.
///   5. On shutdown: send system_powerdown, wait for SHUTDOWN event or process exit.
/// </summary>
public sealed class QmpClient : IAsyncDisposable
{
    private readonly SslStream _ssl;
    private readonly PipeReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<QmpEvent> _events = [];
    private readonly SemaphoreSlim _eventLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement?>> _pending = [];
    private readonly SemaphoreSlim _pendingLock = new(1, 1);
    private bool _disposed;

    private QmpClient(SslStream ssl)
    {
        _ssl = ssl;
        _reader = PipeReader.Create(ssl);
    }

    public static async Task<QmpClient> ConnectAsync(
        string host,
        int port,
        X509Certificate2 clientCert,
        X509Certificate2 caCert,
        CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);

        var caCertRef = caCert; // captured for validation lambda
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
            (_, serverCert, _, _) =>
            {
                if (serverCert is null) return false;
                var serverCert2 = X509CertificateLoader.LoadCertificate(
                    serverCert.Export(X509ContentType.Cert));
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(caCertRef);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(serverCert2);
            });

        var clientCerts = new X509CertificateCollection { clientCert };
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ClientCertificates = clientCerts,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13
                                    | System.Security.Authentication.SslProtocols.Tls12,
            }, ct);

        var client = new QmpClient(ssl);
        await client.ConsumeGreetingAsync(ct);
        await client.SendCapabilitiesAsync(ct);
        _ = client.ReadEventsLoop(client._cts.Token);
        return client;
    }

    // ── Public operations ─────────────────────────────────────────────────

    public async Task<JsonElement?> ExecuteAsync(
        string command,
        Dictionary<string, object?>? args = null,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var req = new QmpRequest { Execute = command, Id = id, Arguments = args };
        var bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(req, ProtocolJsonContext.Default.QmpRequest) + "\n");

        // ── Register BEFORE writing ──────────────────────────────────────────
        // A fast QEMU reply that arrives before we call WaitAsync would be
        // discarded by ReadEventsLoop (no matching TCS). Register first.
        var tcs = new TaskCompletionSource<JsonElement?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _pendingLock.WaitAsync(ct);
        _pending[id] = tcs;
        _pendingLock.Release();

        // Cancel: mark the TCS and best-effort remove from _pending so
        // ReadEventsLoop's TrySet* on a cancelled TCS are no-ops rather than
        // leaking an entry.
        using var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            if (_pendingLock.Wait(0))
            {
                _pending.Remove(id);
                _pendingLock.Release();
            }
        });

        // ── Write ────────────────────────────────────────────────────────────
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await _ssl.WriteAsync(bytes, ct);
                await _ssl.FlushAsync(ct);
            }
            finally { _writeLock.Release(); }
        }
        catch (Exception ex)
        {
            // Write failed — no reply will arrive; remove and fault the TCS.
            await _pendingLock.WaitAsync(CancellationToken.None);
            _pending.Remove(id);
            _pendingLock.Release();
            tcs.TrySetException(ex);
            throw;
        }

        return await tcs.Task;
    }

    public Task SendPowerdownAsync(CancellationToken ct = default) =>
        ExecuteAsync("system_powerdown", ct: ct);

    public async Task<bool> WaitForShutdownEventAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(200, cts.Token);
                await _eventLock.WaitAsync(cts.Token);
                try { if (_events.Any(e => e.Event == "SHUTDOWN")) return true; }
                finally { _eventLock.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        return false;
    }

    // ── Private protocol helpers ──────────────────────────────────────────

    private async Task ConsumeGreetingAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct);
        var greeting = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.QmpGreeting);
        if (greeting?.Qmp is null)
            throw new InvalidDataException($"Expected QMP greeting, got: {line}");
    }

    private async Task SendCapabilitiesAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new QmpCapabilities(), ProtocolJsonContext.Default.QmpCapabilities);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _ssl.WriteAsync(bytes, ct);
        await _ssl.FlushAsync(ct);
        await ReadLineAsync(ct); // consume empty return
    }

    // WaitForResponseAsync removed: TCS is now registered in ExecuteAsync
    // before the write so that fast replies are never lost.

    private async Task ReadEventsLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("event", out _))
                {
                    var ev = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.QmpEvent);
                    if (ev is not null)
                    {
                        await _eventLock.WaitAsync(ct);
                        try { _events.Add(ev); }
                        finally { _eventLock.Release(); }
                    }
                }
                else if (doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    var id = idEl.GetString();
                    if (id is not null)
                    {
                        var resp = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.QmpResponse);
                        await _pendingLock.WaitAsync(ct);
                        if (_pending.TryGetValue(id, out var tcs))
                        {
                            _pending.Remove(id);
                            _pendingLock.Release();
                            if (resp?.Error is not null)
                                tcs.TrySetException(new InvalidOperationException(
                                    $"QMP error {resp.Error.Class}: {resp.Error.Desc}"));
                            else
                                tcs.TrySetResult(resp?.Return);
                        }
                        else { _pendingLock.Release(); }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(ct);
            var buffer = result.Buffer;
            var pos = buffer.PositionOf((byte)'\n');
            if (pos.HasValue)
            {
                var slice = buffer.Slice(0, pos.Value);
                var line = Encoding.UTF8.GetString(slice);
                _reader.AdvanceTo(buffer.GetPosition(1, pos.Value));
                return line.Trim();
            }
            if (result.IsCompleted) return string.Empty;
            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _cts.CancelAsync();
        await _ssl.DisposeAsync();
        _cts.Dispose();
    }
}
