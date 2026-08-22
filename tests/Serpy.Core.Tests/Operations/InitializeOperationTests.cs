using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;

namespace Serpy.Core.Tests.Operations;

public sealed class InitializeOperationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"SerpyInitTest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private InitializeGuardDouble MakeGuard()
    {
        Directory.CreateDirectory(_dir);
        return new InitializeGuardDouble(_dir);
    }

    [Fact]
    public async Task Guard_CommittedMarkerExists_FailsImmediately()
    {
        var guard = MakeGuard();
        File.WriteAllText(Path.Combine(_dir, ".data-committed"), "committed:2026-01-01T00:00:00Z");

        var result = await guard.CheckAsync();

        Assert.NotNull(result);
        Assert.Equal(OperationOutcome.Failure, result!.Outcome);
        Assert.Contains("committed data disk already exists", result.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Guard_FinalDataImgExists_FailsImmediately()
    {
        var guard = MakeGuard();
        File.WriteAllText(Path.Combine(_dir, "data.img"), "fake-disk");

        var result = await guard.CheckAsync();

        Assert.NotNull(result);
        Assert.Equal(OperationOutcome.Failure, result!.Outcome);
    }

    [Fact]
    public async Task Guard_NeitherMarkerNorImg_ReturnsNull_MeansAllowed()
    {
        var guard = MakeGuard();

        var result = await guard.CheckAsync();

        Assert.Null(result); // null = guard passed, proceed
    }
}

/// <summary>
/// Exposes the initialization guard logic in isolation — no QEMU/QGA/resolver needed.
/// Mirrors the guard check at the top of InitializeOperation.ExecuteAsync exactly.
/// </summary>
internal sealed class InitializeGuardDouble(string applianceDir)
{
    private string DataMarkerPath => Path.Combine(applianceDir, ".data-committed");
    private string FinalDataPath  => Path.Combine(applianceDir, "data.img");

    public Task<OperationResult?> CheckAsync()
    {
        if (File.Exists(DataMarkerPath) || File.Exists(FinalDataPath))
            return Task.FromResult<OperationResult?>(new OperationResult(
                Guid.Empty, OperationKind.Initialize, OperationOutcome.Failure,
                "A committed data disk already exists. " +
                "Recover replaces the system image only; existing data is never silently overwritten.",
                null));

        return Task.FromResult<OperationResult?>(null);
    }
}
