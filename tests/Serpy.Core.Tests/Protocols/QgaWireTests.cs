using System.Text.Json;
using Serpy.Core.Protocols.Json;
using Serpy.Core.Protocols.Qga;
using Serpy.Core.Protocols.Qmp;

namespace Serpy.Core.Tests.Protocols;

/// <summary>
/// The guest agent and the monitor validate each message strictly. A field that is present with a
/// null value is a type error to them: the first real build failed on <c>"env": null</c>. These tests
/// pin the bytes that go on the wire.
/// </summary>
public sealed class QgaWireTests
{
    private static JsonElement Wire(QgaExecRequest request) =>
        JsonDocument.Parse(JsonSerializer.Serialize(request, ProtocolJsonContext.Default.QgaExecRequest)).RootElement;

    [Fact]
    public void AnExecWithNoEnvironmentAndNoStdin_LeavesBothFieldsOut_InsteadOfWritingNull()
    {
        var wire = Wire(new QgaExecRequest
        {
            Arguments = new QgaExecArgs { Path = "python3.14", Arg = ["--version"], CaptureOutput = true },
        });

        var arguments = wire.GetProperty("arguments");
        Assert.False(arguments.TryGetProperty("env", out _), "env must be absent, not null");
        Assert.False(arguments.TryGetProperty("input-data", out _), "input-data must be absent, not null");
        Assert.Equal("python3.14", arguments.GetProperty("path").GetString());
        Assert.Equal("--version", arguments.GetProperty("arg")[0].GetString());
        Assert.True(arguments.GetProperty("capture-output").GetBoolean());
    }

    [Fact]
    public void AnExecWithNoArguments_LeavesArgOut()
    {
        var wire = Wire(new QgaExecRequest { Arguments = new QgaExecArgs { Path = "/bin/true" } });

        Assert.False(wire.GetProperty("arguments").TryGetProperty("arg", out _));
    }

    [Fact]
    public void AnExecWithStdin_KeepsItAsAString_AndNoOtherNullAppears()
    {
        var wire = Wire(new QgaExecRequest
        {
            Arguments = new QgaExecArgs { Path = "su", InputData = "c2VjcmV0" },
        });

        Assert.Equal("c2VjcmV0", wire.GetProperty("arguments").GetProperty("input-data").GetString());
        Assert.DoesNotContain("null", wire.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestWithAnEnvironment_WritesItAsAnArray()
    {
        var wire = Wire(new QgaExecRequest
        {
            Arguments = new QgaExecArgs { Path = "env", Env = ["A=1", "B=2"] },
        });

        var env = wire.GetProperty("arguments").GetProperty("env");
        Assert.Equal(JsonValueKind.Array, env.ValueKind);
        Assert.Equal(2, env.GetArrayLength());
    }

    [Fact]
    public void TheStatusRequest_CarriesOnlyThePid()
    {
        var json = JsonSerializer.Serialize(
            new QgaExecStatusRequest { Arguments = new QgaExecStatusArgs { Pid = 4242 } },
            ProtocolJsonContext.Default.QgaExecStatusRequest);

        Assert.Equal(4242, JsonDocument.Parse(json).RootElement.GetProperty("arguments").GetProperty("pid").GetInt32());
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AQmpCommandWithNoArguments_LeavesTheArgumentsOut()
    {
        var json = JsonSerializer.Serialize(
            new QmpRequest { Execute = "system_powerdown" }, ProtocolJsonContext.Default.QmpRequest);

        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.Equal("system_powerdown", JsonDocument.Parse(json).RootElement.GetProperty("execute").GetString());
    }
}

/// <summary>
/// Connecting to the agent of a guest that has only just been started. The first real Initialize
/// connected ten seconds after the boot, got no answer, and reported itself as cancelled.
/// </summary>
public sealed class QgaConnectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"serpy-qga-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Rig(System.Net.Sockets.TcpListener listener, System.Security.Cryptography.X509Certificates.X509Certificate2 serverCert,
        System.Security.Cryptography.X509Certificates.X509Certificate2 client, System.Security.Cryptography.X509Certificates.X509Certificate2 ca) : IDisposable
    {
        public int Port => ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        public System.Net.Sockets.TcpListener Listener => listener;
        public System.Security.Cryptography.X509Certificates.X509Certificate2 ServerCert => serverCert;
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Client => client;
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Ca => ca;
        public void Dispose() { listener.Stop(); serverCert.Dispose(); client.Dispose(); ca.Dispose(); }
    }

    private Rig Start()
    {
        var store = new Serpy.Core.Qemu.TlsCertificateStore(_dir);
        store.GenerateCertificates();
        using var pem = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(
            Path.Combine(store.QemuCertDir, "server-cert.pem"), Path.Combine(store.QemuCertDir, "server-key.pem"));
        var serverCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            pem.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12), password: null);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return new Rig(listener, serverCert, store.LoadClientCert(), store.LoadCaCert());
    }

    // One connection of the fake agent. It answers the sync request only when told to, like a guest
    // whose agent is not running yet: the connection is accepted by QEMU but nothing replies.
    private static async Task ServeOneAsync(Rig rig, bool answer)
    {
        using var tcp = await rig.Listener.AcceptTcpClientAsync();
        using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
        {
            ServerCertificate = rig.ServerCert,
            ClientCertificateRequired = true,
        });

        var request = new List<byte>();
        var one = new byte[1];
        while (await ssl.ReadAsync(one) == 1)
        {
            if (one[0] == 0xFF) continue;
            if (one[0] == (byte)'\n') break;
            request.Add(one[0]);
        }

        if (!answer)
        {
            // Say nothing and hold the connection until the client gives up and closes it. Reading is how
            // the fake notices the close; a plain delay would keep it, and the next accept, waiting.
            try { while (await ssl.ReadAsync(one) > 0) { } }
            catch (IOException) { }
            return;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(request.ToArray());
        var id = doc.RootElement.GetProperty("arguments").GetProperty("id").GetInt32();
        await ssl.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"return\": {id}}}\n"));
        await ssl.FlushAsync();
        await Task.Delay(200);
    }

    [Fact]
    public async Task AnAgentThatNeverAnswers_GivesATimeout_NotACancel_WhenNobodyCancelled()
    {
        using var rig = Start();
        _ = ServeOneAsync(rig, answer: false);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Serpy.Core.Protocols.Qga.QgaClient.ConnectAsync(
            "127.0.0.1", rig.Port, rig.Client, rig.Ca, syncTimeout: TimeSpan.FromMilliseconds(300)));

        Assert.Contains("sync timed out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallersOwnCancel_IsStillACancel()
    {
        using var rig = Start();
        _ = ServeOneAsync(rig, answer: false);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Serpy.Core.Protocols.Qga.QgaClient.ConnectAsync(
            "127.0.0.1", rig.Port, rig.Client, rig.Ca, cts.Token, syncTimeout: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ConnectWhenReady_RetriesUntilTheAgentAnswers_OnALaterConnection()
    {
        using var rig = Start();
        _ = Task.Run(async () =>
        {
            await ServeOneAsync(rig, answer: false); // the first connection: the agent is not up yet
            await ServeOneAsync(rig, answer: true);  // the second: it is
        });

        await using var qga = await Serpy.Core.Protocols.Qga.QgaClient.ConnectWhenReadyAsync(
            "127.0.0.1", rig.Port, rig.Client, rig.Ca,
            wait: TimeSpan.FromSeconds(20), attemptTimeout: TimeSpan.FromMilliseconds(300), retryDelay: TimeSpan.FromMilliseconds(50));

        Assert.NotNull(qga);
    }

    [Fact]
    public async Task ConnectWhenReady_GivesUpWithATimeoutThatNamesTheAgent()
    {
        using var rig = Start();
        _ = Task.Run(async () => { while (true) await ServeOneAsync(rig, answer: false); });

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Serpy.Core.Protocols.Qga.QgaClient.ConnectWhenReadyAsync(
            "127.0.0.1", rig.Port, rig.Client, rig.Ca,
            wait: TimeSpan.FromSeconds(2), attemptTimeout: TimeSpan.FromMilliseconds(200), retryDelay: TimeSpan.FromMilliseconds(50)));

        Assert.Contains("guest agent did not answer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectWhenReady_StopsAtOnce_WhenTheCallerCancels()
    {
        using var rig = Start();
        _ = Task.Run(async () => { while (true) await ServeOneAsync(rig, answer: false); });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Serpy.Core.Protocols.Qga.QgaClient.ConnectWhenReadyAsync(
            "127.0.0.1", rig.Port, rig.Client, rig.Ca, wait: TimeSpan.FromMinutes(5), ct: cts.Token,
            attemptTimeout: TimeSpan.FromMilliseconds(200), retryDelay: TimeSpan.FromMilliseconds(50)));
    }
}
