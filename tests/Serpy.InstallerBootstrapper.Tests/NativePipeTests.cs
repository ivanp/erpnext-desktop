using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Serpy.InstallerBootstrapper.Tests;

/// <summary>
/// Unit tests for <see cref="NativePipe.CreateDaclRestricted"/> -- the
/// authenticated pipe channel's DACL enforcement (IU1 step 7). Proves the
/// OS itself denies a connect attempt from a principal that is not the
/// claimed SID, before any application-level impersonation check even runs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativePipeTests
{
    [Fact]
    public async Task CreateDaclRestricted_AllowedSid_CanConnect()
    {
        var pipeName = $"serpy-test-{Guid.NewGuid():N}";
        var mySid = WindowsIdentity.GetCurrent().User!;

        using var handle = NativePipe.CreateDaclRestricted(pipeName, mySid);
        await using var server = new NamedPipeServerStream(
            PipeDirection.InOut, isAsync: true, isConnected: false, handle);

        var acceptTask = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(5000, cts.Token);
        await acceptTask;

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task CreateDaclRestricted_DisallowedSid_ConnectFails()
    {
        var pipeName = $"serpy-test-{Guid.NewGuid():N}";
        // A well-known SID that is never the current process's own user SID
        // (NULL SID, S-1-0-0) -- the DACL grants access to this SID only,
        // which this test process can never satisfy, so the OS must deny
        // the connect attempt before any application code runs.
        var nullSid = new SecurityIdentifier(WellKnownSidType.NullSid, null);

        using var handle = NativePipe.CreateDaclRestricted(pipeName, nullSid);
        await using var server = new NamedPipeServerStream(
            PipeDirection.InOut, isAsync: true, isConnected: false, handle);

        var acceptTask = server.WaitForConnectionAsync();
        _ = acceptTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // ConnectAsync retries internally until its timeout; a DACL-denied
        // pipe never accepts, so this must time out rather than connect.
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(2000, cts.Token));
        Assert.False(client.IsConnected);
    }
}
