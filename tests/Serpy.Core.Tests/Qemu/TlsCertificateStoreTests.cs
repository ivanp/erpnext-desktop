using System.Security.Cryptography.X509Certificates;
using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Qemu;

public sealed class TlsCertificateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"serpy-certs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void LoadClientCert_HasPrivateKey()
    {
        var store = new TlsCertificateStore(_dir);
        store.GenerateCertificates();

        using var cert = store.LoadClientCert();

        Assert.True(cert.HasPrivateKey);
    }

    /// <summary>
    /// SslStream client-certificate authentication on Windows acquires SChannel credentials via
    /// AcquireCredentialsHandle, which requires a CNG/CAPI-persisted private key. A certificate
    /// built directly from CreateFromPem carries an ephemeral in-memory key that SChannel rejects
    /// with "Authentication failed, see inner exception." / "credentials supplied to the package
    /// were not recognized" at credential-acquisition time, before any network I/O reaches a peer.
    /// Exercise the real mTLS handshake end to end over a loopback SslStream server requiring a
    /// client certificate — the same shape QEMU's tls-creds-x509,verify-peer=yes requires — so this
    /// regresses if LoadClientCert ever goes back to returning the raw CreateFromPem certificate.
    /// </summary>
    [Fact]
    public async Task LoadClientCert_AuthenticatesOverLoopbackMutualTls()
    {
        var store = new TlsCertificateStore(_dir);
        store.GenerateCertificates();

        using var serverCert = X509Certificate2.CreateFromPemFile(
            Path.Combine(store.QemuCertDir, "server-cert.pem"),
            Path.Combine(store.QemuCertDir, "server-key.pem"));
        using var serverCertWithKey = X509CertificateLoader.LoadPkcs12(
            serverCert.Export(X509ContentType.Pkcs12), password: null);
        using var caCert = store.LoadCaCert();

        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var tcpServer = await listener.AcceptTcpClientAsync();
            using var sslServer = new System.Net.Security.SslStream(tcpServer.GetStream(), false,
                (_, _, _, _) => true);
            await sslServer.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertWithKey,
                ClientCertificateRequired = true,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                    | System.Security.Authentication.SslProtocols.Tls13,
            });
        });

        using var tcpClient = new System.Net.Sockets.TcpClient();
        await tcpClient.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var sslClient = new System.Net.Security.SslStream(tcpClient.GetStream(), false,
            (_, serverPresented, _, _) =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(caCert);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return serverPresented is not null && chain.Build((X509Certificate2)serverPresented);
            });

        using var clientCert = store.LoadClientCert();
        await sslClient.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ClientCertificates = [clientCert],
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                | System.Security.Authentication.SslProtocols.Tls13,
        });

        await serverTask;
        Assert.True(sslClient.IsAuthenticated);
        Assert.True(sslClient.IsMutuallyAuthenticated);
    }
}
