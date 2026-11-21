using System.Runtime.Versioning;
using System.Text.Json;
using Serpy.Core.Qemu;
using Xunit;

namespace Serpy.InstallerBootstrapper.Tests;

[SupportedOSPlatform("windows")]
public sealed class FeatureEnableTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"SerpyDismTest-{Guid.NewGuid():N}");
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    public FeatureEnableTests()
    {
        Directory.CreateDirectory(_tempDir);
        DismRunner.TestExecutor = null;
    }

    public void Dispose()
    {
        DismRunner.TestExecutor = null;
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DismRunner_StartsDismBySystemFolderFullPath_IgnoringEarlierDismOnPath()
    {
        // Place a fake dism.exe in a temp directory and prepend it to PATH
        var fakeDism = Path.Combine(_tempDir, "dism.exe");
        File.WriteAllText(fakeDism, "fake dism");
        Environment.SetEnvironmentVariable("PATH", $"{_tempDir};{_originalPath}");

        string? executedPath = null;
        DismRunner.TestExecutor = (path, _, _) =>
        {
            executedPath = path;
            return Task.FromResult(0);
        };

        var exitCode = await DismRunner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(executedPath);
        var expectedSystemDism = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
        Assert.Equal(expectedSystemDism, executedPath, ignoreCase: true);
        Assert.NotEqual(fakeDism, executedPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DismRunner_PassesFixedArguments_NamingBothFeatures()
    {
        string[]? executedArgs = null;
        DismRunner.TestExecutor = (_, args, _) =>
        {
            executedArgs = args;
            return Task.FromResult(0);
        };

        await DismRunner.RunAsync(CancellationToken.None);

        Assert.NotNull(executedArgs);
        string[] expected =
        [
            "/Online",
            "/Quiet",
            "/NoRestart",
            "/Enable-Feature",
            "/All",
            "/FeatureName:HypervisorPlatform",
            "/FeatureName:VirtualMachinePlatform"
        ];
        Assert.Equal(expected, executedArgs);
    }

    [Fact]
    public async Task DismRunner_WhenDismReports3010_Returns3010()
    {
        DismRunner.TestExecutor = (_, _, _) => Task.FromResult(3010);

        var exitCode = await DismRunner.RunAsync(CancellationToken.None);

        Assert.Equal(3010, exitCode);
    }

    [Fact]
    public async Task DismRunner_WhenDismFails_ReturnsExitCode()
    {
        DismRunner.TestExecutor = (_, _, _) => Task.FromResult(50);

        var exitCode = await DismRunner.RunAsync(CancellationToken.None);

        Assert.Equal(50, exitCode);
    }

    [Fact]
    public void BootstrapClientHello_WithUnknownField_IsRejectedByDeserializer()
    {
        // A caller trying to pass an extra field like "commandLine" must be rejected
        // so that no caller text can ever influence execution (KTD14)
        const string maliciousJson = """
            {
                "nonce": "n1",
                "installerPath": "C:\\fake\\installer.exe",
                "commandLine": "calc.exe"
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(maliciousJson, BootstrapWireJsonContext.Default.BootstrapClientHello));
    }

    [Fact]
    public void BootstrapClientHello_WithFeatureFlag_RoundTripsThroughWireContext()
    {
        var testPath = Path.Combine("C:", "test", "path.exe");
        var original = new BootstrapClientHello("test-nonce", testPath, EnableWindowsFeatures: true);
        var json = JsonSerializer.Serialize(original, BootstrapWireJsonContext.Default.BootstrapClientHello);

        var deserialized = JsonSerializer.Deserialize(json, BootstrapWireJsonContext.Default.BootstrapClientHello);

        Assert.NotNull(deserialized);
        Assert.Equal("test-nonce", deserialized.Nonce);
        Assert.Equal(testPath, deserialized.InstallerPath);
        Assert.True(deserialized.EnableWindowsFeatures);
    }

    [Fact]
    public void BootstrapWireResult_WithDismExitCode3010_RoundTripsThroughWireContext()
    {
        var original = new BootstrapWireResult(0, "C:\\staging", null, DismExitCode: 3010);
        var json = JsonSerializer.Serialize(original, BootstrapWireJsonContext.Default.BootstrapWireResult);

        var deserialized = JsonSerializer.Deserialize(json, BootstrapWireJsonContext.Default.BootstrapWireResult);

        Assert.NotNull(deserialized);
        Assert.Equal(0, deserialized.ExitCode);
        Assert.Equal("C:\\staging", deserialized.StagingDir);
        Assert.Equal(3010, deserialized.DismExitCode);
    }

    [Fact]
    public void FeatureRequest_FailingNonceCheck_IsRefused()
    {
        var validNonce = BootstrapProtocol.CreateNonce();
        var mismatchedNonce = BootstrapProtocol.CreateNonce();
        Assert.False(BootstrapProtocol.NoncesMatch(validNonce, mismatchedNonce));
    }

    [Fact]
    public void FeatureRequest_WithInvalidSourceFile_ThrowsBeforeElevationAction()
    {
        // Missing file must throw InvalidOperationException
        var missing = Path.Combine(_tempDir, "does-not-exist.exe");
        Assert.Throws<InvalidOperationException>(() =>
            BootstrapProtocol.ValidateSourceFile(missing));
    }
}
