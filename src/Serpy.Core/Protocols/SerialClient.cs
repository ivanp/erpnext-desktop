using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Serpy.Core.Protocols;

/// <summary>
/// Serial-console chardev client over mTLS (KTD9).
/// Used during cloud-init provisioning before the guest agent exists.
/// Reads lines from the serial console, which carry the provisioning markers and command output.
/// Both serial and QGA share the same per-install TLS trust material.
/// </summary>
public sealed class SerialClient : IAsyncDisposable
{
    /// <summary>Longest a TCP connect to the serial port may take.</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Longest the mutual-TLS handshake may take after the connection is open.</summary>
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly SslStream _ssl;
    private bool _disposed;

    private SerialClient(SslStream ssl) => _ssl = ssl;

    public static Task<SerialClient> ConnectAsync(
        string host,
        int port,
        X509Certificate2 clientCert,
        X509Certificate2 caCert,
        CancellationToken ct = default) =>
        ConnectAsync(host, port, clientCert, caCert, DefaultConnectTimeout, DefaultHandshakeTimeout, ct);

    /// <summary>
    /// Connects and authenticates. A connect or handshake that exceeds its timeout throws a
    /// <see cref="TimeoutException"/> whose message names the phase, so a hang is never silent.
    /// </summary>
    public static async Task<SerialClient> ConnectAsync(
        string host,
        int port,
        X509Certificate2 clientCert,
        X509Certificate2 caCert,
        TimeSpan connectTimeout,
        TimeSpan handshakeTimeout,
        CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(connectTimeout);
                try { await tcp.ConnectAsync(host, port, connectCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Could not connect to the guest console on port {port} within {connectTimeout.TotalSeconds:0} seconds.");
                }
            }

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
            using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshakeCts.CancelAfter(handshakeTimeout);
                try
                {
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = host,
                            ClientCertificates = clientCerts,
                            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13
                                                | System.Security.Authentication.SslProtocols.Tls12,
                        }, handshakeCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await ssl.DisposeAsync();
                    throw new TimeoutException(
                        $"The secure handshake with the guest console did not finish within {handshakeTimeout.TotalSeconds:0} seconds.");
                }
            }

            return new SerialClient(ssl);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Yields one entry per console line. The sequence ends when the peer closes the connection,
    /// so a closed console is never read again in a loop. A trailing partial line is yielded.
    /// </summary>
    public async IAsyncEnumerable<string> ReadLines([EnumeratorCancellation] CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        while (true)
        {
            var n = await _ssl.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (sb.Length > 0) yield return sb.ToString().TrimEnd('\r');
                yield break;
            }

            for (var i = 0; i < n; i++)
            {
                if (buf[i] == (byte)'\n')
                {
                    yield return sb.ToString().TrimEnd('\r');
                    sb.Clear();
                }
                else
                {
                    sb.Append((char)buf[i]);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _ssl.DisposeAsync();
    }
}
