using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Serpy.Core.Protocols;

/// <summary>
/// Serial-console chardev client over mTLS (KTD9).
/// Used during cloud-init provisioning before the guest agent exists.
/// Reads lines from the serial console looking for the provision-done sentinel.
/// Both serial and QGA share the same per-install TLS trust material.
/// </summary>
public sealed class SerialClient : IAsyncDisposable
{
    private readonly SslStream _ssl;
    private bool _disposed;

    private SerialClient(SslStream ssl) => _ssl = ssl;

    public static async Task<SerialClient> ConnectAsync(
        string host,
        int port,
        X509Certificate2 clientCert,
        X509Certificate2 caCert,
        CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);

        var caCertRef = caCert;
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

        return new SerialClient(ssl);
    }

    /// <summary>
    /// Read lines until <paramref name="sentinel"/> appears or timeout elapses.
    /// Returns all lines read (available under Show Details).
    /// </summary>
    public async Task<SerialReadResult> WaitForSentinelAsync(
        string sentinel,
        TimeSpan timeout,
        IProgress<string>? lineProgress = null,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var lines = new List<string>();
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await ReadLineAsync(cts.Token);
                lines.Add(line);
                lineProgress?.Report(line);
                if (line.Contains(sentinel, StringComparison.Ordinal))
                    return new SerialReadResult(Found: true, Lines: lines);
            }
        }
        catch (OperationCanceledException) { }

        return new SerialReadResult(Found: false, Lines: lines);
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var n = await _ssl.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0) return sb.ToString();
            if (buf[0] == (byte)'\n') return sb.ToString().TrimEnd('\r');
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

public sealed record SerialReadResult(bool Found, IReadOnlyList<string> Lines);
