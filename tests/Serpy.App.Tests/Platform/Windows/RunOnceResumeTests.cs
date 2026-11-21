using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Serpy.App.Platform.Windows;
using Xunit;

namespace Serpy.App.Tests.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class RunOnceResumeTests : IDisposable
{
    private readonly string _testSubKey = $@"SOFTWARE\SerpyTest_{Guid.NewGuid():N}\RunOnce";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"SerpyRunOnceTest-{Guid.NewGuid():N}");

    public RunOnceResumeTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_testSubKey.Split('\\')[0], throwOnMissingSubKey: false); } catch { }
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveValueName_ScopesToAppDataRoot()
    {
        var name1 = RunOnceResume.ResolveValueName(@"C:\Users\Alice\AppData\Local\Serpy");
        var name2 = RunOnceResume.ResolveValueName(@"C:\Users\Bob\AppData\Local\Serpy");
        var name3 = RunOnceResume.ResolveValueName(@"C:\Users\Alice\AppData\Local\Serpy");

        Assert.StartsWith("SerpyResume_", name1);
        Assert.NotEqual(name1, name2);
        Assert.Equal(name1, name3);
    }

    [Fact]
    public void Enable_WritesResumeSetupCommand_AndDisableCleansUp()
    {
        var fakeExe = Path.Combine(_tempDir, "Serpy.App.exe");
        File.WriteAllText(fakeExe, "fake binary");

        var appData = Path.Combine(_tempDir, "appdata");
        Directory.CreateDirectory(appData);

        // Before enable: not present
        Assert.False(RunOnceResume.IsEnabled(appData, _testSubKey));

        // Enable
        RunOnceResume.Enable(fakeExe, appData, _testSubKey);
        Assert.True(RunOnceResume.IsEnabled(appData, _testSubKey));

        var command = RunOnceResume.GetRegisteredCommand(appData, _testSubKey);
        Assert.NotNull(command);
        Assert.Equal($"\"{fakeExe}\" --resume-setup", command);

        // Disable
        RunOnceResume.Disable(appData, _testSubKey);
        Assert.False(RunOnceResume.IsEnabled(appData, _testSubKey));
        Assert.Null(RunOnceResume.GetRegisteredCommand(appData, _testSubKey));
    }

    [Fact]
    public void Enable_MissingFile_ThrowsAutostartException()
    {
        var missingPath = Path.Combine(_tempDir, "nonexistent.exe");
        Assert.Throws<AutostartException>(() =>
            RunOnceResume.Enable(missingPath, subKeyPath: _testSubKey));
    }

    [Fact]
    public void Enable_CommandTooLong_ThrowsAutostartException()
    {
        var longSubdir = Path.Combine(_tempDir, new string('a', 230));
        Directory.CreateDirectory(longSubdir);
        var longPath = Path.Combine(longSubdir, "verylongname.exe");
        File.WriteAllText(longPath, "binary");

        Assert.Throws<AutostartException>(() =>
            RunOnceResume.Enable(longPath, subKeyPath: _testSubKey));
    }
}
