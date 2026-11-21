using System.Security.Cryptography;
using System.Text;
using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Downloads;
using Serpy.Core.Images;
using Serpy.Core.Operations;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Operations;

[Collection("LifecycleLock")]
public sealed class BuildOperationTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _prevAppData;

    public BuildOperationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SerpyBuildOpTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _prevAppData = Environment.GetEnvironmentVariable("SERPY_TEST_APPDATA") ?? string.Empty;
        Environment.SetEnvironmentVariable("SERPY_TEST_APPDATA", _testRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SERPY_TEST_APPDATA", string.IsNullOrEmpty(_prevAppData) ? null : _prevAppData);
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, recursive: true); } catch { }
        }
    }

    private static VersionManifest CreateManifest() => new()
    {
        DebianCloudImage = new VersionManifest.DebianCloudImageSection
        {
            Version = "20260819-2575",
            Url = "https://example.com/base.qcow2",
            Sha512 = "dummy-sha"
        },
        Runtime = new VersionManifest.RuntimeSection
        {
            Python = new VersionManifest.LockFloor { Lock = "3.14.7", Floor = "3.14.0" },
            Node = new VersionManifest.LockFloor { Lock = "24.2.0", Floor = "24.0.0" },
            MariaDb = new VersionManifest.LockFloor { Lock = "11.8.3", Floor = "11.8.0" },
            Redis = new VersionManifest.LockFloor { Lock = "8.0.2", Floor = "8.0.0" },
        },
        Apps = new VersionManifest.AppsSection
        {
            Frappe = new VersionManifest.AppEntry { Branch = "v16.0.0", Commit = "6a329d068416768ec47ccd3326b9cc95a8d7bf99", Lock = "16.0.0", MinVersion = "16.0.0" },
            ErpNext = new VersionManifest.AppEntry { Branch = "v16.0.0", Commit = "11e0ba0a1c45f217e2e73e885f699102d06da325", Lock = "16.0.0", MinVersion = "16.0.0" },
        },
    };

    private static (BuildInputs Inputs, DownloadCache Cache) CreateValidInputs(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        var cache = new DownloadCache(cacheDir);
        var content = Encoding.UTF8.GetBytes("test deb package content");
        var filePath = cache.GetPath("deb1");
        File.WriteAllBytes(filePath, content);

        var sha256 = Convert.ToHexString(SHA256.HashData(content));
        var inputs = new BuildInputs
        {
            Parts =
            [
                new InputPart
                {
                    Id = "system-tools",
                    Files =
                    [
                        new InputFile
                        {
                            Key = "deb1",
                            Path = "debs/test.deb",
                            Size = content.Length,
                            Sha256 = sha256,
                            InGuest = true
                        }
                    ]
                }
            ]
        };

        return (inputs, cache);
    }

    [Fact]
    public async Task InsufficientFreeSpace_ReturnsDiskFull_BeforePacking_LeavesNoFiles()
    {
        var (inputs, cache) = CreateValidInputs(Path.Combine(_testRoot, "cache"));
        var stateStore = new StateStore(Path.Combine(_testRoot, "state.json"));
        var certStore = new TlsCertificateStore(Path.Combine(_testRoot, "certs"));
        var manifest = CreateManifest();
        var settings = new ApplianceSettings();

        var op = new BuildOperation(
            imageDownloader: null,
            seedWriter: null,
            imageTool: null,
            runtimeResolver: null,
            certStore: certStore,
            stateStore: stateStore,
            manifest: manifest,
            settings: settings,
            buildInputs: inputs,
            downloadCache: cache,
            freeSpaceBytes: () => 1024L); // Only 1 KB available

        var result = await op.ExecuteAsync(Guid.NewGuid(), new Progress<OperationUpdate>(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.DiskFull, result.Code);
        Assert.False(File.Exists(op.TransportTarPath));
        Assert.False(File.Exists(op.ScratchDiskPath));
    }

    [Fact]
    public async Task ChangedCacheFile_FailsChecksumMismatch_AndBuildDoesNotStart()
    {
        var (inputs, cache) = CreateValidInputs(Path.Combine(_testRoot, "cache"));
        // Tamper with cached file
        var diskPath = cache.GetPath("deb1");
        File.WriteAllBytes(diskPath, Encoding.UTF8.GetBytes("tampered bad bytes"));

        var stateStore = new StateStore(Path.Combine(_testRoot, "state.json"));
        var certStore = new TlsCertificateStore(Path.Combine(_testRoot, "certs"));
        var manifest = CreateManifest();
        var settings = new ApplianceSettings();

        bool qemuStarted = false;
        var op = new BuildOperation(
            imageDownloader: null,
            seedWriter: null,
            imageTool: null,
            runtimeResolver: null,
            certStore: certStore,
            stateStore: stateStore,
            manifest: manifest,
            settings: settings,
            buildInputs: inputs,
            downloadCache: cache,
            freeSpaceBytes: () => 50L * 1024 * 1024 * 1024,
            startProcess: (_, _, _, _, _) =>
            {
                qemuStarted = true;
                return QemuProcess.Start("cmd.exe", ["/c", "exit 0"]);
            });

        var result = await op.ExecuteAsync(Guid.NewGuid(), new Progress<OperationUpdate>(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.ChecksumMismatch, result.Code);
        Assert.False(qemuStarted, "QEMU build process must not start when checksum mismatch occurs.");
        Assert.False(File.Exists(op.TransportTarPath));
        Assert.False(File.Exists(op.ScratchDiskPath));
    }

    [Fact]
    public async Task BuildArguments_ContainRestrictedNet_TransportDisk_AndScratchDisk()
    {
        var (inputs, cache) = CreateValidInputs(Path.Combine(_testRoot, "cache"));
        var stateStore = new StateStore(Path.Combine(_testRoot, "state.json"));
        var certStore = new TlsCertificateStore(Path.Combine(_testRoot, "certs"));
        var manifest = CreateManifest();
        var settings = new ApplianceSettings();

        IReadOnlyList<string>? capturedArgs = null;

        var op = new BuildOperation(
            imageDownloader: null,
            seedWriter: null,
            imageTool: null,
            runtimeResolver: null,
            certStore: certStore,
            stateStore: stateStore,
            manifest: manifest,
            settings: settings,
            buildInputs: inputs,
            downloadCache: cache,
            freeSpaceBytes: () => 50L * 1024 * 1024 * 1024,
            createSparseRaw: (path, size, ct) =>
            {
                File.WriteAllBytes(path, new byte[512]);
                return Task.CompletedTask;
            },
            startProcess: (exe, args, logPath, log, endWithSerpy) =>
            {
                capturedArgs = args;
                return QemuProcess.Start("cmd.exe", ["/c", "exit 0"]);
            },
            connectSerialLines: (port, clientCert, caCert, ct) =>
            {
                // Guest fails immediately
                async IAsyncEnumerable<string> Lines()
                {
                    yield return "SERPY_PROVISION_STARTED";
                    yield return "SERPY_STAGE os-packages";
                    yield return "SERPY_PROVISION_FAILED os-packages 1";
                }
                return Task.FromResult(Lines());
            });

        // Create base image dummy to satisfy staging image creation
        var baseImage = Path.Combine(KnownPaths.ApplianceDir, "debian-base.qcow2");
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
        File.WriteAllBytes(baseImage, new byte[512]);

        await op.ExecuteAsync(Guid.NewGuid(), new Progress<OperationUpdate>(), CancellationToken.None);

        Assert.NotNull(capturedArgs);
        Assert.Contains(capturedArgs, a => a.Contains("restrict=on", StringComparison.Ordinal));
        Assert.Contains(capturedArgs, a => a.Contains("serial=serpycache", StringComparison.Ordinal));
        Assert.Contains(capturedArgs, a => a.Contains("readonly=on", StringComparison.Ordinal));
        Assert.Contains(capturedArgs, a => a.Contains("serial=serpyscratch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuestStageFailure_ReturnsProvisioningFailed_NamingTheStage()
    {
        var (inputs, cache) = CreateValidInputs(Path.Combine(_testRoot, "cache"));
        var stateStore = new StateStore(Path.Combine(_testRoot, "state.json"));
        var certStore = new TlsCertificateStore(Path.Combine(_testRoot, "certs"));
        var manifest = CreateManifest();
        var settings = new ApplianceSettings();


        var op = new BuildOperation(
            imageDownloader: null,
            seedWriter: null,
            imageTool: null,
            runtimeResolver: null,
            certStore: certStore,
            stateStore: stateStore,
            manifest: manifest,
            settings: settings,
            buildInputs: inputs,
            downloadCache: cache,
            freeSpaceBytes: () => 50L * 1024 * 1024 * 1024,
            createSparseRaw: (path, size, ct) =>
            {
                File.WriteAllBytes(path, new byte[512]);
                return Task.CompletedTask;
            },
            startProcess: (exe, args, logPath, log, endWithSerpy) =>
            {
                return QemuProcess.Start("cmd.exe", ["/c", "ping -n 3 127.0.0.1 > nul"]);
            },
            connectSerialLines: (port, clientCert, caCert, ct) =>
            {
                async IAsyncEnumerable<string> Lines()
                {
                    yield return "SERPY_PROVISION_STARTED";
                    yield return "SERPY_STAGE language-tools";
                    yield return "SERPY_PROVISION_FAILED language-tools 42";
                }
                return Task.FromResult(Lines());
            });

        var baseImage = Path.Combine(KnownPaths.ApplianceDir, "debian-base.qcow2");
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
        File.WriteAllBytes(baseImage, new byte[512]);

        var result = await op.ExecuteAsync(Guid.NewGuid(), new Progress<OperationUpdate>(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.ProvisioningFailed, result.Code);
        Assert.Contains("language-tools", result.Message);
        Assert.False(File.Exists(op.TransportTarPath));
        Assert.False(File.Exists(op.ScratchDiskPath));
    }

    [Fact]
    public async Task TransportAndScratchFiles_AreCleanedUp_OnSuccess()
    {
        var (inputs, cache) = CreateValidInputs(Path.Combine(_testRoot, "cache"));
        var stateStore = new StateStore(Path.Combine(_testRoot, "state.json"));
        var certStore = new TlsCertificateStore(Path.Combine(_testRoot, "certs"));
        var manifest = CreateManifest();
        var settings = new ApplianceSettings();


        var op = new BuildOperation(
            imageDownloader: null,
            seedWriter: null,
            imageTool: null,
            runtimeResolver: null,
            certStore: certStore,
            stateStore: stateStore,
            manifest: manifest,
            settings: settings,
            buildInputs: inputs,
            downloadCache: cache,
            freeSpaceBytes: () => 50L * 1024 * 1024 * 1024,
            createSparseRaw: (path, size, ct) =>
            {
                File.WriteAllBytes(path, new byte[512]);
                return Task.CompletedTask;
            },
            startProcess: (exe, args, logPath, log, endWithSerpy) =>
            {
                return QemuProcess.Start("cmd.exe", ["/c", "ping -n 2 127.0.0.1 > nul"]);
            },
            connectSerialLines: (port, clientCert, caCert, ct) =>
            {
                async IAsyncEnumerable<string> Lines()
                {
                    yield return "SERPY_PROVISION_STARTED";
                    yield return "SERPY_STAGE os-packages";
                    yield return "SERPY_STAGE database";
                    yield return "SERPY_STAGE language-tools";
                    yield return "SERPY_STAGE erpnext-source";
                    yield return "SERPY_STAGE erpnext-assets";
                    yield return "SERPY_STAGE services";
                    yield return "SERPY_PROVISION_DONE";
                }
                return Task.FromResult(Lines());
            },
            queryVersions: (port, clientCert, caCert, ct) => Task.FromResult(new GuestVersionReport
            {
                Python = "3.14.7",
                Node = "24.2.0",
                MariaDb = "11.8.3",
                Redis = "8.0.2",
                Frappe = "16.0.0",
                ErpNext = "16.0.0"
            }),
            sendPowerdown: (port, clientCert, caCert, ct) => Task.CompletedTask);

        var baseImage = Path.Combine(KnownPaths.ApplianceDir, "debian-base.qcow2");
        Directory.CreateDirectory(KnownPaths.ApplianceDir);
        File.WriteAllBytes(baseImage, new byte[512]);

        var result = await op.ExecuteAsync(Guid.NewGuid(), new Progress<OperationUpdate>(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.True(File.Exists(op.FinalPath));
        Assert.False(File.Exists(op.TransportTarPath));
        Assert.False(File.Exists(op.ScratchDiskPath));
    }
}
