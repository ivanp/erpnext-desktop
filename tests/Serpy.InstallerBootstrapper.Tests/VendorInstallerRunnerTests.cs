using System.Runtime.Versioning;
using Serpy.InstallerBootstrapper;
using Xunit;

namespace Serpy.InstallerBootstrapper.Tests;

[SupportedOSPlatform("windows")]
public sealed class VendorInstallerRunnerTests : IDisposable
{
    public void Dispose()
    {
        VendorInstallerRunner.TestExecutor = null;
        DismRunner.TestExecutor = null;
    }

    [Fact]
    public async Task VendorInstallerRunner_WhenTestExecutorReturnsZero_ReturnsZero()
    {
        VendorInstallerRunner.TestExecutor = (installer, staging, ct) => Task.FromResult(0);

        var result = await VendorInstallerRunner.RunAsync("fake.exe", "fakeDir", CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task VendorInstallerRunner_WhenTestExecutorReturnsNonZero_ReturnsExitCode()
    {
        VendorInstallerRunner.TestExecutor = (installer, staging, ct) => Task.FromResult(42);

        var result = await VendorInstallerRunner.RunAsync("fake.exe", "fakeDir", CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task VendorInstallerRunner_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        VendorInstallerRunner.TestExecutor = (installer, staging, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            VendorInstallerRunner.RunAsync("fake.exe", "fakeDir", cts.Token));
    }

    [Fact]
    public async Task DismRunner_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        DismRunner.TestExecutor = (path, args, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DismRunner.RunAsync(cts.Token));
    }
}
