using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;
using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Operations;

[Collection("LifecycleLock")] // sets the process-wide app-data override, so it must not run beside other tests
public sealed class StopOperationLogTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyStopLog-{Guid.NewGuid():N}");
    private readonly string? _previous = Environment.GetEnvironmentVariable(OverrideVariable);

    public StopOperationLogTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(OverrideVariable, _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previous);
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Stop_WithNothingRunning_LeavesALogThatStatusPointsTo()
    {
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        var op = new StopOperation(store, new TlsCertificateStore(Path.Combine(_dir, "certs")));

        var result = await op.ExecuteAsync(Guid.NewGuid(), new Progress<OperationUpdate>(u => { }), CancellationToken.None);
        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal(result.LogPath, status.LogPath);
        using var reader = new StreamReader(new FileStream(result.LogPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        Assert.Contains("nothing to stop", reader.ReadToEnd(), StringComparison.Ordinal);
    }
}
