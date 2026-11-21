using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Serpy.Core.Protocols.Json;

namespace Serpy.Core.Protocols.Qga;

/// <summary>
/// Guest Agent client over the QEMU-side virtio-serial chardev socket with mTLS (KTD3).
/// Protocol:
///   1. Send 0xFF flush byte.
///   2. Send guest-sync-delimited; consume until our ID is echoed back.
///   3. guest-exec / guest-exec-status for all in-guest commands.
///   4. Decode base64 output without a 64 KiB line-size assumption.
/// The endpoint is the QEMU chardev socket address, NOT a guest TCP listener.
/// </summary>
public sealed class QgaClient : IAsyncDisposable
{
    /// <summary>
    /// How long one connection waits for the agent to answer its sync request. A guest that is still
    /// booting does not answer, so callers that start a VM use <see cref="ConnectWhenReadyAsync"/>.
    /// </summary>
    public static readonly TimeSpan DefaultSyncTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a freshly started guest has to bring its agent up.</summary>
    public static readonly TimeSpan DefaultReadyWait = TimeSpan.FromMinutes(5);

    private readonly SslStream _ssl;
    private bool _disposed;

    private QgaClient(SslStream ssl) => _ssl = ssl;

    public static async Task<QgaClient> ConnectAsync(
        string host,
        int port,
        X509Certificate2 clientCert,
        X509Certificate2 caCert,
        CancellationToken ct = default,
        TimeSpan? syncTimeout = null)
    {
        var limit = syncTimeout ?? DefaultSyncTimeout;

        // One time limit covers the whole attempt: the TCP connect, the TLS handshake and the sync. A
        // chardev that QEMU has accepted but nobody serves yet can hang any of the three, and a hang in
        // the first two would otherwise outlast the retry deadline of the caller.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(limit);

        TcpClient? tcp = null;
        SslStream? ssl = null;
        try
        {
            tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, attempt.Token);

            var caCertRef = caCert;
            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
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

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ClientCertificates = new X509CertificateCollection { clientCert },
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13
                                        | System.Security.Authentication.SslProtocols.Tls12,
                }, attempt.Token);

            var client = new QgaClient(ssl);
            await client.SyncAsync(attempt.Token);
            return client;
        }
        catch (Exception ex)
        {
            // Whatever failed, nothing of this attempt may stay open: QEMU serves one client per
            // chardev, so a half-open connection would stall the next attempt.
            if (ssl is not null) await ssl.DisposeAsync();
            tcp?.Dispose();

            // The attempt timer ending a step is a timeout. Only the token of the caller is a cancel;
            // reporting the timer as one made Initialize say it was cancelled while the guest booted.
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new TimeoutException("QGA sync timed out (connect, handshake or sync did not finish in time).", ex);
            throw;
        }
    }

    /// <summary>
    /// Connects to the agent of a guest that has just been started. The agent comes up some seconds
    /// after the boot, and until then the sync request gets no answer, so this tries again until
    /// <paramref name="wait"/> has passed. It ends early only when <paramref name="ct"/> is cancelled,
    /// and then it throws <see cref="OperationCanceledException"/>. A guest that never answers gives a
    /// <see cref="TimeoutException"/> that names the agent.
    /// </summary>
    public static async Task<QgaClient> ConnectWhenReadyAsync(
        string host,
        int port,
        X509Certificate2 clientCert,
        X509Certificate2 caCert,
        TimeSpan? wait = null,
        CancellationToken ct = default,
        TimeSpan? attemptTimeout = null,
        TimeSpan? retryDelay = null)
    {
        var deadline = DateTime.UtcNow + (wait ?? DefaultReadyWait);
        var delay = retryDelay ?? TimeSpan.FromSeconds(2);
        Exception? last = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await ConnectAsync(host, port, clientCert, caCert, ct, attemptTimeout);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or SocketException
                                          or System.Security.Authentication.AuthenticationException)
            {
                last = ex;
            }

            if (DateTime.UtcNow + delay >= deadline)
                throw new TimeoutException(
                    $"The guest agent did not answer within {(wait ?? DefaultReadyWait).TotalSeconds:0} seconds. " +
                    $"Last problem: {last.Message}", last);

            await Task.Delay(delay, ct);
        }
    }

    // ── Sync ──────────────────────────────────────────────────────────────

    private async Task SyncAsync(CancellationToken ct)
    {
        await _ssl.WriteAsync(new byte[] { 0xFF }, ct);
        await _ssl.FlushAsync(ct);

        var syncReq = new QgaSyncRequest();
        var expectedId = syncReq.Arguments.Id;
        var json = JsonSerializer.Serialize(syncReq, ProtocolJsonContext.Default.QgaSyncRequest);
        await WriteLineAsync(json, ct);

        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("return", out var ret)
                    && ret.TryGetInt32(out var id) && id == expectedId)
                    return;
            }
            catch (JsonException) { }
        }
    }

    // ── guest-exec ────────────────────────────────────────────────────────

    public async Task<GuestExecResult> ExecAsync(
        string path,
        string[]? args = null,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var req = new QgaExecRequest
        {
            Arguments = new QgaExecArgs
            {
                Path = path,
                Arg = args,
                CaptureOutput = true,
                InputData = stdin is not null
                    ? Convert.ToBase64String(Encoding.UTF8.GetBytes(stdin))
                    : null,
            },
        };

        var json = JsonSerializer.Serialize(req, ProtocolJsonContext.Default.QgaExecRequest);
        await WriteLineAsync(json, ct);
        var respLine = await ReadLineAsync(ct);
        var execResult = JsonSerializer.Deserialize(respLine, ProtocolJsonContext.Default.QgaExecResult);
        var pid = execResult?.Return?.Pid
            ?? throw new InvalidDataException($"QGA exec returned no PID: {respLine}");

        var deadline = timeout.HasValue
            ? DateTime.UtcNow + timeout.Value
            : DateTime.UtcNow + TimeSpan.FromMinutes(10);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var statusReq = new QgaExecStatusRequest { Arguments = new QgaExecStatusArgs { Pid = pid } };
            var statusJson = JsonSerializer.Serialize(statusReq, ProtocolJsonContext.Default.QgaExecStatusRequest);
            await WriteLineAsync(statusJson, ct);
            var statusLine = await ReadLineAsync(ct);
            var status = JsonSerializer.Deserialize(statusLine, ProtocolJsonContext.Default.QgaExecStatusResult);
            var ret = status?.Return;
            if (ret is null || !ret.Exited) continue;

            var stdout = ret.OutData is not null
                ? Encoding.UTF8.GetString(Convert.FromBase64String(ret.OutData))
                : string.Empty;
            var stderr = ret.ErrData is not null
                ? Encoding.UTF8.GetString(Convert.FromBase64String(ret.ErrData))
                : string.Empty;

            return new GuestExecResult(ret.ExitCode, stdout, stderr);
        }

        throw new TimeoutException($"QGA guest-exec timed out for: {path}");
    }

    // ── Low-level I/O ─────────────────────────────────────────────────────

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _ssl.WriteAsync(bytes, ct);
        await _ssl.FlushAsync(ct);
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await _ssl.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0) return sb.ToString();
            if (buf[0] == (byte)'\n') return sb.ToString().Trim();
            if (buf[0] != 0xFF)
                sb.Append((char)buf[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _ssl.DisposeAsync();
    }
}

public sealed record GuestExecResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}
