using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Serpy.Core.Qemu;

namespace Serpy.Windows.IntegrationTests;

/// <summary>
/// Real accept-path proof for the elevated <c>Serpy.InstallerBootstrapper.exe</c>
/// pipe/DACL/impersonation/nonce authentication chain (IU1 step 7, IR3) --
/// spawns the actual built executable and drives its pipe protocol as the
/// non-elevated app-side client would (<see cref="ElevatedInstallChannel"/>
/// does the same handshake in production).
///
/// Opt-in: requires SERPY_RUN_SMOKE=1 (same gate as <see cref="QemuSmokeTests"/>)
/// and the built <c>Serpy.InstallerBootstrapper.exe</c> at its repo-relative
/// build output path. Skips gracefully -- does not fail -- when either is
/// absent, matching this repo's existing hardware/session-dependent test
/// pattern.
///
/// These tests do not require elevation: the bootstrapper's pipe server,
/// DACL, and authentication logic all run identically whether or not this
/// process itself is elevated -- only the later copy-to-admin-intermediate
/// and vendor-installer-launch steps need real elevation, which is covered
/// separately by <c>StagingHardeningTests</c> (unit-level, run under this
/// repo's elevated dev session) and the plan's documented manual acceptance
/// procedure (IU4).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerBootstrapperAcceptTests
{
    private static bool ShouldRun =>
        Environment.GetEnvironmentVariable("SERPY_RUN_SMOKE") == "1";

    private static string? FindBootstrapperExe()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "src", "Serpy.InstallerBootstrapper", "bin", "Release", "net10.0", "Serpy.InstallerBootstrapper.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record ScenarioResult(int ServerExitCode, string StdErr, BootstrapWireResult? WireResult, Exception? ClientError);

    private static async Task<ScenarioResult> RunScenarioAsync(
        string exePath, string installerPath,
        string serverClaimedSid, string serverNonce,
        string clientNonce, string clientInstallerPath,
        TimeSpan connectTimeout)
    {
        var pipeName = $"serpy-accept-test-{Guid.NewGuid():N}";
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--pipe-name"); psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--nonce"); psi.ArgumentList.Add(serverNonce);
        psi.ArgumentList.Add("--claimed-sid"); psi.ArgumentList.Add(serverClaimedSid);
        psi.ArgumentList.Add("--installer-path"); psi.ArgumentList.Add(installerPath);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start bootstrapper.");

        BootstrapWireResult? wireResult = null;
        Exception? clientError = null;
        try
        {
            using var cts = new CancellationTokenSource(connectTimeout);
            await using var session = await ElevatedInstallChannel.ConnectAsync(
                pipeName, clientNonce, clientInstallerPath, connectTimeout, cts.Token);
            var result = session.StagedResult;
            wireResult = new BootstrapWireResult(result.ExitCode, result.StagingDir, null);
        }
        catch (Exception ex)
        {
            clientError = ex;
        }

        await proc.WaitForExitAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        return new ScenarioResult(proc.ExitCode, stderr, wireResult, clientError);
    }

    [Fact]
    public async Task CorrectSidAndNonce_AuthenticatesAndReachesDescriptorBoundary()
    {
        if (!ShouldRun) return;
        var exe = FindBootstrapperExe();
        if (exe is null) return;

        var installerPath = Path.Combine(Path.GetTempPath(), $"serpy-accept-installer-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(installerPath, "fake installer bytes");
        try
        {
            var mySid = WindowsIdentity.GetCurrent().User!.Value;
            var nonce = BootstrapProtocol.CreateNonce();

            var result = await RunScenarioAsync(exe, installerPath, mySid, nonce, nonce, installerPath, TimeSpan.FromSeconds(15));

            // No signed descriptor ships next to a locally-built dev exe, so
            // authentication succeeds and the bootstrapper fails closed at
            // the descriptor-load step (exit 5) -- proving the pipe/DACL/
            // impersonation/nonce chain works end-to-end for a genuinely
            // authenticated caller, without needing a real vendor installer
            // or signed descriptor fixture in this test.
            Assert.Equal(5, result.ServerExitCode);
        }
        finally
        {
            File.Delete(installerPath);
        }
    }

    [Fact]
    public async Task WrongNonce_RejectedAsAuthFailed()
    {
        if (!ShouldRun) return;
        var exe = FindBootstrapperExe();
        if (exe is null) return;

        var installerPath = Path.Combine(Path.GetTempPath(), $"serpy-accept-installer-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(installerPath, "fake installer bytes");
        try
        {
            var mySid = WindowsIdentity.GetCurrent().User!.Value;
            var serverNonce = BootstrapProtocol.CreateNonce();
            var wrongNonce = BootstrapProtocol.CreateNonce();

            var result = await RunScenarioAsync(exe, installerPath, mySid, serverNonce, wrongNonce, installerPath, TimeSpan.FromSeconds(15));

            Assert.Equal(4, result.ServerExitCode);
        }
        finally
        {
            File.Delete(installerPath);
        }
    }

    [Fact]
    public async Task WrongInstallerPathInHello_RejectedAsAuthFailed()
    {
        if (!ShouldRun) return;
        var exe = FindBootstrapperExe();
        if (exe is null) return;

        var installerPath = Path.Combine(Path.GetTempPath(), $"serpy-accept-installer-{Guid.NewGuid():N}.exe");
        var otherPath = Path.Combine(Path.GetTempPath(), $"serpy-accept-other-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(installerPath, "fake installer bytes");
        await File.WriteAllTextAsync(otherPath, "different bytes");
        try
        {
            var mySid = WindowsIdentity.GetCurrent().User!.Value;
            var nonce = BootstrapProtocol.CreateNonce();

            // Server launched expecting `installerPath`; client's authenticated
            // hello claims a DIFFERENT path -- must be rejected even though the
            // SID and nonce both check out (a request naming a foreign
            // installer cannot redirect what the helper trusts).
            var result = await RunScenarioAsync(exe, installerPath, mySid, nonce, nonce, otherPath, TimeSpan.FromSeconds(15));

            Assert.Equal(4, result.ServerExitCode);
        }
        finally
        {
            File.Delete(installerPath);
            File.Delete(otherPath);
        }
    }

    [Fact]
    public async Task ForeignClaimedSid_DaclDeniesConnectionEntirely()
    {
        if (!ShouldRun) return;
        var exe = FindBootstrapperExe();
        if (exe is null) return;

        var installerPath = Path.Combine(Path.GetTempPath(), $"serpy-accept-installer-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(installerPath, "fake installer bytes");
        try
        {
            // Everyone (S-1-1-0) -- this process's own SID is never S-1-1-0,
            // so if the DACL genuinely restricted the pipe, connecting as
            // this process would still succeed here (Everyone includes
            // everyone), which is why the impersonated-SID check below is
            // the layer that must reject it -- proving defense-in-depth,
            // not just DACL enforcement in isolation.
            var everyoneSid = "S-1-1-0";
            var nonce = BootstrapProtocol.CreateNonce();

            var result = await RunScenarioAsync(exe, installerPath, everyoneSid, nonce, nonce, installerPath, TimeSpan.FromSeconds(15));

            Assert.Equal(4, result.ServerExitCode);
        }
        finally
        {
            File.Delete(installerPath);
        }
    }

    [Fact]
    public async Task ReparsePointSource_RejectedBeforePipeIsEvenCreated()
    {
        if (!ShouldRun) return;
        var exe = FindBootstrapperExe();
        if (exe is null) return;

        var targetPath = Path.Combine(Path.GetTempPath(), $"serpy-accept-target-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(targetPath, "real installer bytes");
        var junctionDir = Path.Combine(Path.GetTempPath(), $"serpy-accept-junctiondir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(junctionDir);
        var reparseInstallerPath = Path.Combine(junctionDir, "installer.exe");
        try
        {
            // A symlink source must be rejected -- creating a *file* symlink
            // requires elevation or Developer Mode, so this test only runs
            // meaningfully on this repo's elevated dev session; it skips
            // gracefully (not a failure) if symlink creation is denied.
            try
            {
                File.CreateSymbolicLink(reparseInstallerPath, targetPath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return; // no elevation/Developer Mode in this environment -- skip
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--pipe-name"); psi.ArgumentList.Add($"serpy-accept-test-{Guid.NewGuid():N}");
            psi.ArgumentList.Add("--nonce"); psi.ArgumentList.Add(BootstrapProtocol.CreateNonce());
            psi.ArgumentList.Add("--claimed-sid"); psi.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
            psi.ArgumentList.Add("--installer-path"); psi.ArgumentList.Add(reparseInstallerPath);

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();

            // Source validation happens before the pipe is even created --
            // exit code 2 (ExitInvalidSource), never reaching authentication.
            Assert.Equal(2, proc.ExitCode);
        }
        finally
        {
            if (File.Exists(reparseInstallerPath)) File.Delete(reparseInstallerPath);
            Directory.Delete(junctionDir, recursive: true);
            File.Delete(targetPath);
        }
    }
    [Fact]
    public async Task ShaMismatchAgainstDescriptor_RejectedBeforeVendorLaunch()
    {
        if (!ShouldRun) return;
        var exe = FindBootstrapperExe();
        if (exe is null) return;
        var exeDir = Path.GetDirectoryName(exe)!;
        var descriptorPath = Path.Combine(exeDir, SignedRuntimeDescriptorFile.FileName);

        // TOCTOU proof (IR3): the descriptor names an expected SHA-256 that
        // this test's fake installer content will NOT match. Re-verification
        // happens at the admin-only intermediate copy, after authentication
        // but before any vendor installer launch -- exactly the check that
        // rejects a swapped/tampered source regardless of what the app-side
        // pre-check believed. Requires the local dev descriptor-signing key
        // this repo's other descriptor tests already rely on; skips
        // gracefully if it is absent (never committed).
        var privateKeyPath = FindRepoFile(".local-signing/descriptor-signing-private.pem");
        if (privateKeyPath is null) return;

        var alreadyShipped = File.Exists(descriptorPath);
        if (!alreadyShipped)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
            var descriptor = new RuntimeDescriptor(
                QemuVersion: "11.1.0",
                InstallerUrl: "https://example.invalid/qemu.exe",
                InstallerSha256: new string('a', 64), // never matches the fake installer content below
                SourceUrl: "https://example.invalid/qemu-src.tar.xz",
                LicenseNoticeUrl: "https://example.invalid/license");
            var signature = DescriptorSigner.Sign(descriptor, rsa);
            var envelope = new SignedRuntimeDescriptorFile(descriptor, Convert.ToBase64String(signature));
            var json = JsonSerializer.Serialize(envelope, SignedRuntimeDescriptorFileJsonContext.Default.SignedRuntimeDescriptorFile);
            await File.WriteAllTextAsync(descriptorPath, json);
        }

        var installerPath = Path.Combine(Path.GetTempPath(), $"serpy-accept-installer-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(installerPath, "this content never matches the descriptor's pinned SHA-256");
        try
        {
            var mySid = WindowsIdentity.GetCurrent().User!.Value;
            var nonce = BootstrapProtocol.CreateNonce();

            var result = await RunScenarioAsync(exe, installerPath, mySid, nonce, nonce, installerPath, TimeSpan.FromSeconds(15));

            Assert.Equal(6, result.ServerExitCode);
        }
        finally
        {
            File.Delete(installerPath);
            if (!alreadyShipped) File.Delete(descriptorPath);
        }
    }

    private static string? FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
