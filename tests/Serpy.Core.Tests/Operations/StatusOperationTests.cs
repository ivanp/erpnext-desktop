using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;

namespace Serpy.Core.Tests.Operations;

public sealed class StatusOperationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyStatusTest-{Guid.NewGuid():N}");

    public StatusOperationTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetStatus_NotBuiltNoData_RecommendsSetupRoute()
    {
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        var op = new StatusOperation(store);

        var status = await op.GetStatusAsync();

        Assert.Equal(ReadinessState.NotBuilt, status.Readiness);
        Assert.False(status.HasCommittedDataOnDisk);
        Assert.Equal(LaunchRoute.Setup, status.RecommendedRoute);
    }

    [Fact]
    public async Task GetStatus_Initialized_RecommendsStartRoute()
    {
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        store.Mutate(s => s.Readiness = ReadinessState.Initialized);

        var op = new StatusOperation(store);
        var status = await op.GetStatusAsync();

        Assert.Equal(ReadinessState.Initialized, status.Readiness);
        Assert.Equal(LaunchRoute.Start, status.RecommendedRoute);
    }
}
