using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;

namespace Serpy.Core.Tests.Operations;

// StatusOperation looks for committed data in the app-data folder. Against the real folder, a
// data.img from a real setup changes the route these tests expect, so they use a temporary root.
[Collection("LifecycleLock")]
public sealed class StatusOperationTests : IDisposable
{
    private const string OverrideVariable = "SERPY_TEST_APPDATA";
    private readonly string? _previousOverride = Environment.GetEnvironmentVariable(OverrideVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyStatusTest-{Guid.NewGuid():N}");

    public StatusOperationTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(OverrideVariable, Path.Combine(_dir, "appdata"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OverrideVariable, _previousOverride);
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

    [Fact]
    public async Task GetStatus_ExposesTheLogPathOfTheLastOperation()
    {
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        store.Mutate(s => s.LogPath = Path.Combine(_dir, "build-1.log"));

        var status = await new StatusOperation(store).GetStatusAsync();

        Assert.Equal(Path.Combine(_dir, "build-1.log"), status.LogPath);
    }
}
