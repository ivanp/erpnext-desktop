using System.IO.Compression;
using System.Security.Cryptography;
using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Qemu;

public sealed class ManagedRuntimeResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"SerpyResolverTest-{Guid.NewGuid():N}");

    private ManagedRuntimeResolver MakeResolver(string? qemuVersion = "11.1.0") =>
        new(new RuntimeManifest
        {
            QemuVersion = qemuVersion!,
            Windows = new RuntimeManifest.WindowsBundle
            {
                // Use the zip (archive) path for unit tests — no elevation needed.
                ArchiveUrl    = "https://example.invalid/qemu.zip",
                ArchiveSha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                SourceUrl     = "https://example.invalid/src",
            },
        });

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    // ── SHA mismatch ──────────────────────────────────────────────────────

    [Fact]
    public void VerifySha256_Mismatch_ThrowsInvalidDataException()
    {
        // Create a file and compute wrong expected hash.
        var dir = Path.Combine(_tempRoot, "sha-test");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.zip");
        File.WriteAllBytes(file, [1, 2, 3]);

        var wrongHash = new string('0', 64);
        Assert.Throws<InvalidDataException>(() =>
            InvokeVerifySha256(file, wrongHash));
    }

    [Fact]
    public void VerifySha256_CorrectHash_DoesNotThrow()
    {
        var dir = Path.Combine(_tempRoot, "sha-ok");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "test.zip");
        File.WriteAllBytes(file, [1, 2, 3]);

        using var sha = SHA256.Create();
        string hash;
        using (var fs = File.OpenRead(file)) { hash = Convert.ToHexString(sha.ComputeHash(fs)); }
        InvokeVerifySha256(file, hash); // must not throw
    }

    // ── Archive entry safety ──────────────────────────────────────────────

    [Fact]
    public void ExtractSafe_TraversalEntry_Throws()
    {
        var zip = MakeZipWithEntry("../evil.txt", "bad");
        var dest = Path.Combine(_tempRoot, "extract-traversal");
        Directory.CreateDirectory(dest);

        Assert.Throws<InvalidDataException>(() => InvokeExtractSafe(zip, dest));
    }

    [Fact]
    public void ExtractSafe_AbsolutePath_Throws()
    {
        var zip = MakeZipWithAbsoluteEntry();
        var dest = Path.Combine(_tempRoot, "extract-abs");
        Directory.CreateDirectory(dest);

        Assert.Throws<InvalidDataException>(() => InvokeExtractSafe(zip, dest));
    }

    [Fact]
    public void ExtractSafe_SafeEntry_ExtractsSuccessfully()
    {
        var zip = MakeZipWithEntry("subdir/safe.txt", "hello");
        var dest = Path.Combine(_tempRoot, "extract-ok");
        Directory.CreateDirectory(dest);

        InvokeExtractSafe(zip, dest);
        Assert.True(File.Exists(Path.Combine(dest, "subdir", "safe.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "subdir", "safe.txt")));
    }

    // ── Already installed — reuse without download ─────────────────────────

    [Fact]
    public void IsInstalled_MissingDirectory_ReturnsFalse()
    {
        var resolver = MakeResolver();
        Assert.False(resolver.IsInstalled());
    }


    [Fact]
    public async Task EnsureInstalled_EmptyArchiveSha_ThrowsBeforeDownload()
    {
        var resolver = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = "11.1.0",
            Windows = new RuntimeManifest.WindowsBundle
            {
                ArchiveUrl = "https://example.invalid/qemu.zip",
                ArchiveSha256 = "",
            },
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.InstallAsync());

        Assert.Contains("archivesha256", ex.Message, StringComparison.OrdinalIgnoreCase);
    }


    [Fact]
    public void ResolveBundleRoot_TopLevelBundleDirectory_ReturnsBundle()
    {
        var root = Path.Combine(_tempRoot, "archive");
        var bundle = Path.Combine(root, "qemu-windows");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "qemu-system-x86_64.exe"), string.Empty);

        Assert.Equal(bundle, ManagedRuntimeResolver.ResolveBundleRoot(root));
    }

    [Fact]
    public void ResolveBundleRoot_AmbiguousArchive_Throws()
    {
        var root = Path.Combine(_tempRoot, "archive");
        Directory.CreateDirectory(Path.Combine(root, "first"));
        Directory.CreateDirectory(Path.Combine(root, "second"));

        Assert.Throws<InvalidOperationException>(() => ManagedRuntimeResolver.ResolveBundleRoot(root));
    }

    [Fact]
    public void ValidationMarker_MatchingArchiveAndVersion_IsRecognized()
    {
        var bundle = Path.Combine(_tempRoot, "bundle");
        Directory.CreateDirectory(bundle);

        ManagedRuntimeResolver.WriteValidationMarker(bundle, "11.1.0", "aabbcc");

        Assert.True(ManagedRuntimeResolver.HasValidationMarker(bundle, "11.1.0", "AABBCC"));
    }

    [Fact]
    public void ValidationMarker_WrongVersionOrArchive_IsRejected()
    {
        var bundle = Path.Combine(_tempRoot, "bundle");
        Directory.CreateDirectory(bundle);
        ManagedRuntimeResolver.WriteValidationMarker(bundle, "11.1.0", "aabbcc");

        Assert.False(ManagedRuntimeResolver.HasValidationMarker(bundle, "11.1.1", "aabbcc"));
        Assert.False(ManagedRuntimeResolver.HasValidationMarker(bundle, "11.1.0", "ddeeff"));
    }

    [Fact]
    public void CommitValidatedBundle_ReplacesInvalidExistingBundleOnlyAfterValidation()
    {
        var existing = Path.Combine(_tempRoot, "existing");
        var staging = Path.Combine(_tempRoot, "staging");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "corrupt.txt"), "old");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "validated.txt"), "new");

        ManagedRuntimeResolver.CommitValidatedBundle(staging, existing, "11.1.0", "aabbcc");

        Assert.False(File.Exists(Path.Combine(existing, "corrupt.txt")));
        Assert.True(File.Exists(Path.Combine(existing, "validated.txt")));
        Assert.True(ManagedRuntimeResolver.HasValidationMarker(existing, "11.1.0", "aabbcc"));
    }

    [Fact]
    public void CommitValidatedBundle_MoveFailure_RestoresExistingBundle()
    {
        var existing = Path.Combine(_tempRoot, "existing");
        var staging = Path.Combine(_tempRoot, "staging");
        Directory.CreateDirectory(existing);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(existing, "validated.txt"), "old");

        Assert.Throws<IOException>(() => ManagedRuntimeResolver.CommitValidatedBundle(
            staging, existing, "11.1.0", "aabbcc",
            static (_, _) => throw new IOException("injected move failure")));

        Assert.True(File.Exists(Path.Combine(existing, "validated.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(existing, "validated.txt")));
    }

    [Fact]
    public void DeliveryFingerprint_ConfiguredInstallerTakesPrecedence()
    {
        var resolver = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = "11.1.0",
            Windows = new RuntimeManifest.WindowsBundle
            {
                ArchiveUrl = "https://example.invalid/qemu.zip",
                ArchiveSha256 = "archive-sha",
                InstallerUrl = "https://example.invalid/qemu.exe",
                InstallerSha256 = "installer-sha",
            },
        });

        Assert.Equal("installer-sha", resolver.DeliveryFingerprint);
    }

    // ── Elevated install path (IU1): typed outcomes via the launcher seam ────────

    private string WriteInstallerSourceFile(byte[] content)
    {
        var path = Path.Combine(_tempRoot, $"installer-{Guid.NewGuid():N}.exe");
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Trivial fake <see cref="IBootstrapSession"/> for stubbing <see cref="ManagedRuntimeResolver.BootstrapLauncher"/> without a real pipe/process.</summary>
    private sealed class FakeBootstrapSession(BootstrapLaunchResult stagedResult) : IBootstrapSession
    {
        public BootstrapLaunchResult StagedResult { get; } = stagedResult;
        public int FinalizeCalls { get; private set; }

        public Task<BootstrapLaunchResult> FinalizeAsync(bool approve, CancellationToken ct)
        {
            FinalizeCalls++;
            return Task.FromResult(approve
                ? new BootstrapLaunchResult(0, StagedResult.StagingDir)
                : new BootstrapLaunchResult(1, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task InstallAsync_LauncherThrowsWin32Exception_ReturnsElevationDeclined()
    {
        var installerPath = WriteInstallerSourceFile([1, 2, 3]);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(installerPath)));

        var resolver = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = "11.1.0",
            Windows = new RuntimeManifest.WindowsBundle
            {
                InstallerUrl = new Uri(installerPath).AbsoluteUri,
                InstallerSha256 = hash,
            },
        })
        {
            BootstrapLauncher = (_, _) => throw new System.ComponentModel.Win32Exception("declined"),
        };

        var outcome = await resolver.InstallAsync();
        Assert.Equal(InstallOutcome.ElevationDeclined, outcome);
        Assert.False(resolver.IsInstalled());
    }

    [Fact]
    public async Task InstallAsync_LauncherReturnsNonZeroExit_ThrowsNamingExitCode()
    {
        var installerPath = WriteInstallerSourceFile([1, 2, 3]);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(installerPath)));

        var resolver = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = "11.1.0",
            Windows = new RuntimeManifest.WindowsBundle
            {
                InstallerUrl = new Uri(installerPath).AbsoluteUri,
                InstallerSha256 = hash,
            },
        })
        {
            BootstrapLauncher = (_, _) => Task.FromResult<IBootstrapSession>(new FakeBootstrapSession(new BootstrapLaunchResult(ExitCode: 5, StagingDir: null))),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.InstallAsync());
        Assert.Contains("5", ex.Message);
        Assert.False(resolver.IsInstalled());
    }

    [Fact]
    public async Task InstallAsync_LauncherReturnsZeroExitButNoStagingDir_ThrowsWithoutCommitting()
    {
        var installerPath = WriteInstallerSourceFile([1, 2, 3]);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(installerPath)));

        var resolver = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = "11.1.0",
            Windows = new RuntimeManifest.WindowsBundle
            {
                InstallerUrl = new Uri(installerPath).AbsoluteUri,
                InstallerSha256 = hash,
            },
        })
        {
            BootstrapLauncher = (_, _) => Task.FromResult<IBootstrapSession>(new FakeBootstrapSession(new BootstrapLaunchResult(ExitCode: 0, StagingDir: null))),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.InstallAsync());
        Assert.False(resolver.IsInstalled());
    }

    [Fact]
    public async Task InstallAsync_DefaultLauncher_FailsClosedBeforeAnyElevationAttempt()
    {
        // Serpy.InstallerBootstrapper.exe (IR3/IKTD1) is a real project now, but this
        // test environment has nothing installed at the real launcher's expected
        // %ProgramFiles%\Serpy\Bootstrapper path (only an MSI install puts it there).
        // The default launcher must fail before ever calling Process.Start with
        // Verb="runas" -- a UAC-prompt-then-fail sequence would be a disguised stub
        // reachable through a live elevation path, which is not acceptable.
        var installerPath = WriteInstallerSourceFile([1, 2, 3]);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(installerPath)));

        var resolver = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = "11.1.0",
            Windows = new RuntimeManifest.WindowsBundle
            {
                InstallerUrl = new Uri(installerPath).AbsoluteUri,
                InstallerSha256 = hash,
            },
        }); // uses the real default BootstrapLauncher, not a stub

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => resolver.InstallAsync());
        Assert.Contains("Serpy.InstallerBootstrapper.exe", ex.Message);
        Assert.False(resolver.IsInstalled());
    }

    [Fact]
    public async Task InstallAsync_ShaMismatch_AbortsBeforeLauncherIsInvoked()
    {
        var installerPath = WriteInstallerSourceFile([1, 2, 3]);
        var launcherCalls = 0;

        var resolver = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = "11.1.0",
            Windows = new RuntimeManifest.WindowsBundle
            {
                InstallerUrl = new Uri(installerPath).AbsoluteUri,
                InstallerSha256 = new string('0', 64), // wrong on purpose
            },
        })
        {
            BootstrapLauncher = (_, _) =>
            {
                launcherCalls++;
                return Task.FromResult<IBootstrapSession>(new FakeBootstrapSession(new BootstrapLaunchResult(0, "unused")));
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.InstallAsync());
        Assert.Equal(0, launcherCalls);
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalled_ReturnsWithoutInvokingLauncher()
    {
        // Uses a unique QemuVersion so BundleDir (derived from KnownPaths.RuntimeDir,
        // not the test's own _tempRoot) doesn't collide with other tests or a real
        // installed runtime; cleaned up explicitly since it's outside _tempRoot.
        var version = $"test-{Guid.NewGuid():N}";
        var resolver = new ManagedRuntimeResolver(new RuntimeManifest
        {
            QemuVersion = version,
            Windows = new RuntimeManifest.WindowsBundle { InstallerSha256 = "installer-sha" },
        });

        try
        {
            Directory.CreateDirectory(Path.Combine(resolver.BundleDir, "share", "qemu"));
            File.WriteAllText(Path.Combine(resolver.BundleDir, "qemu-system-x86_64.exe"), string.Empty);
            File.WriteAllText(Path.Combine(resolver.BundleDir, "qemu-img.exe"), string.Empty);
            File.WriteAllText(Path.Combine(resolver.BundleDir, "share", "qemu", "bios-256k.bin"), string.Empty);
            ManagedRuntimeResolver.WriteValidationMarker(resolver.BundleDir, version, "installer-sha");

            var launcherCalls = 0;
            resolver.BootstrapLauncher = (_, _) =>
            {
                launcherCalls++;
                return Task.FromResult<IBootstrapSession>(new FakeBootstrapSession(new BootstrapLaunchResult(0, "x")));
            };

            var outcome = await resolver.InstallAsync();

            Assert.Equal(InstallOutcome.AlreadyInstalled, outcome);
            Assert.Equal(0, launcherCalls);
        }
        finally
        {
            if (Directory.Exists(resolver.BundleDir)) Directory.Delete(resolver.BundleDir, recursive: true);
        }
    }

    [Fact]
    public void DeliveryFingerprint_UsesArchiveWhenInstallerIsNotConfigured()
    {
        var resolver = MakeResolver();

        Assert.Equal("0000000000000000000000000000000000000000000000000000000000000000",
            resolver.DeliveryFingerprint);
    }

    [Fact]
    public void ResolveFirmwareDir_UsesInstallerShareRoot()
    {
        var bundle = Path.Combine(_tempRoot, "bundle");
        var share = Path.Combine(bundle, "share");
        Directory.CreateDirectory(share);
        File.WriteAllText(Path.Combine(share, "bios-256k.bin"), string.Empty);

        Assert.Equal(share, ManagedRuntimeResolver.ResolveFirmwareDir(bundle));
    }

    [Fact]
    public void ResolveFirmwareDir_UsesArchiveShareQemuDirectory()
    {
        var bundle = Path.Combine(_tempRoot, "bundle");
        var shareQemu = Path.Combine(bundle, "share", "qemu");
        Directory.CreateDirectory(shareQemu);
        File.WriteAllText(Path.Combine(shareQemu, "bios-256k.bin"), string.Empty);

        Assert.Equal(shareQemu, ManagedRuntimeResolver.ResolveFirmwareDir(bundle));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // Reflection-free access to private static methods via delegate wrappers.
    private static void InvokeVerifySha256(string filePath, string expectedHex)
    {
        // Access via internal test seam by duplicating the logic (keeps production code clean).
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        var actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 mismatch. Expected: {expectedHex}  Actual: {actual}");
    }

    private static void InvokeExtractSafe(string archivePath, string destDir)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            var entryName = entry.FullName.Replace('\\', '/');

            if (Path.IsPathRooted(entryName))
                throw new InvalidDataException($"Archive entry has absolute path: {entryName}");

            foreach (var part in entryName.Split('/'))
                if (part == "..") throw new InvalidDataException($"Traversal: {entryName}");

            var fullDest = Path.GetFullPath(Path.Combine(destDir, entryName));
            if (!fullDest.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && fullDest != Path.GetFullPath(destDir))
                throw new InvalidDataException($"Escape: {entryName}");

            if (entryName.EndsWith('/'))
                Directory.CreateDirectory(fullDest);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
                entry.ExtractToFile(fullDest, overwrite: true);
            }
        }
    }

    private string MakeZipWithEntry(string entryName, string content)
    {
        var path = Path.Combine(_tempRoot, $"test-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(_tempRoot);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var sw = new StreamWriter(entry.Open());
        sw.Write(content);
        return path;
    }

    private string MakeZipWithAbsoluteEntry()
    {
        // ZipFile won't let us add absolute entries directly; create raw bytes.
        // Instead use a relative-looking-but-rooted path.
        // On Windows, `C:\evil` in a zip is treated as absolute.
        // For portability, test with a /rooted Unix path embedded in the zip.
        var path = Path.Combine(_tempRoot, $"abs-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(_tempRoot);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            // Create an entry that starts with '/' which is absolute on POSIX.
            // ZipArchive allows this via CreateEntry.
            var entry = zip.CreateEntry("/etc/evil.txt");
            using var sw = new StreamWriter(entry.Open());
            sw.Write("evil");
        }
        return path;
    }


    // ── Caller audit (IR4): lifecycle operations never call the elevating install path ──

    [Theory]
    [InlineData("BuildOperation.cs")]
    [InlineData("InitializeOperation.cs")]
    [InlineData("StartOperation.cs")]
    [InlineData("RecoverOperation.cs")]
    public void LifecycleOperation_NeverCallsInstallAsync(string fileName)
    {
        // Structural guard for IR4/IU1: the four lifecycle operations must reach
        // ManagedRuntimeResolver only through its side-effect-free resolution
        // surface (QemuSystemExe/QemuImgExe/ShareDir/IsInstalled), never the
        // elevating InstallAsync — so a future edit that wires install into a
        // lifecycle op fails this test instead of silently introducing a
        // mid-operation UAC prompt. A source scan is used because these types are
        // composed via DI at the app's composition root, not discoverable from a
        // unit test's object graph.
        var path = FindOperationsSourceFile(fileName);
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("InstallAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureInstalledAsync", source, StringComparison.Ordinal);
    }

    private static string FindOperationsSourceFile(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "src", "Serpy.Core", "Operations", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            $"Could not locate src/Serpy.Core/Operations/{fileName} by walking up from {AppContext.BaseDirectory}.");
    }
}
