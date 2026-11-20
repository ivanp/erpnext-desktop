using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Serpy.Core.Protocols;
using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Protocols;

public sealed class SerialClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"serpy-serial-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Server(TcpListener listener, X509Certificate2 clientCert, X509Certificate2 caCert) : IDisposable
    {
        public TcpListener Listener => listener;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public X509Certificate2 ClientCert => clientCert;
        public X509Certificate2 CaCert => caCert;
        public void Dispose() { listener.Stop(); clientCert.Dispose(); caCert.Dispose(); }
    }

    private Server Start(out X509Certificate2 serverCertWithKey)
    {
        var store = new TlsCertificateStore(_dir);
        store.GenerateCertificates();
        using var serverCert = X509Certificate2.CreateFromPemFile(
            Path.Combine(store.QemuCertDir, "server-cert.pem"),
            Path.Combine(store.QemuCertDir, "server-key.pem"));
        serverCertWithKey = X509CertificateLoader.LoadPkcs12(serverCert.Export(X509ContentType.Pkcs12), password: null);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Server(listener, store.LoadClientCert(), store.LoadCaCert());
    }

    private static async Task ServeAsync(TcpListener listener, X509Certificate2 serverCert, Func<Stream, Task> body)
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = serverCert,
            ClientCertificateRequired = true,
        });
        await body(ssl);
    }

    private static async Task<List<string>> Drain(IAsyncEnumerable<string> lines, int max = 100)
    {
        var all = new List<string>();
        await foreach (var l in lines) { all.Add(l); if (all.Count >= max) break; }
        return all;
    }

    [Fact]
    public async Task ReadLines_YieldsEachLine_ThenEndsWhenThePeerCloses()
    {
        using var server = Start(out var serverCert);
        using var serverCertOwner = serverCert;
        var listener = server.Listener;
        var serve = ServeAsync(listener, serverCert, async s =>
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("one\r\ntwo\npartial"));
        });

        await using var client = await SerialClient.ConnectAsync(
            "127.0.0.1", server.Port, server.ClientCert, server.CaCert, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var lines = await Drain(client.ReadLines(CancellationToken.None));
        await serve;

        Assert.Equal(["one", "two", "partial"], lines);
    }

    [Fact]
    public async Task ReadLines_AfterThePeerClosed_ReturnsAtOnceInsteadOfSpinning()
    {
        using var server = Start(out var serverCert);
        using var serverCertOwner = serverCert;
        var serve = ServeAsync(server.Listener, serverCert, body => Task.CompletedTask);

        await using var client = await SerialClient.ConnectAsync(
            "127.0.0.1", server.Port, server.ClientCert, server.CaCert, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        await serve;

        var run = Drain(client.ReadLines(CancellationToken.None), max: 1_000_000);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(finished == run, "ReadLines kept running after the peer closed.");
        Assert.True((await run).Count < 10);
    }

    [Fact]
    public async Task ConnectAsync_ToAPortNobodyAnswers_FailsWithinItsTimeout_AndNamesTheConnectPhase()
    {
        using var server = Start(out var serverCert);
        using var serverCertOwner = serverCert;
        var blackhole = new TcpListener(IPAddress.Loopback, 0);
        blackhole.Start(backlog: 0);
        var port = ((IPEndPoint)blackhole.LocalEndpoint).Port;
        // Fill the backlog so a further connect hangs instead of being accepted.
        var fillers = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var c = new TcpClient();
                fillers.Add(c);
                _ = c.ConnectAsync(IPAddress.Loopback, port);
            }
            await Task.Delay(200);

            var started = DateTime.UtcNow;
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => SerialClient.ConnectAsync(
                "127.0.0.1", port, server.ClientCert, server.CaCert, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10)));

            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
            Assert.Contains("connect", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var c in fillers) c.Dispose();
            blackhole.Stop();
        }
    }

    [Fact]
    public async Task ConnectAsync_ToAPeerThatNeverCompletesTheHandshake_FailsAndNamesTheHandshakePhase()
    {
        using var server = Start(out var serverCert);
        using var serverCertOwner = serverCert;
        var listener = server.Listener;
        var accept = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            await Task.Delay(TimeSpan.FromSeconds(5)); // accepts the TCP connection, then says nothing
        });

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => SerialClient.ConnectAsync(
            "127.0.0.1", server.Port, server.ClientCert, server.CaCert, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500)));

        Assert.Contains("handshake", ex.Message, StringComparison.OrdinalIgnoreCase);
        await accept;
    }
}
