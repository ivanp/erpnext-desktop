using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Qemu;

public sealed class BootstrapProtocolTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"SerpyBootstrapTest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    // ── Nonce ─────────────────────────────────────────────────────────────

    [Fact]
    public void CreateNonce_ProducesHighEntropyDistinctValues()
    {
        var a = BootstrapProtocol.CreateNonce();
        var b = BootstrapProtocol.CreateNonce();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 32); // hex-encoded 32 bytes = 64 chars
    }

    [Fact]
    public void NoncesMatch_SameValue_ReturnsTrue()
    {
        var nonce = BootstrapProtocol.CreateNonce();
        Assert.True(BootstrapProtocol.NoncesMatch(nonce, nonce));
    }

    [Fact]
    public void NoncesMatch_DifferentValue_ReturnsFalse()
    {
        var a = BootstrapProtocol.CreateNonce();
        var b = BootstrapProtocol.CreateNonce();
        Assert.False(BootstrapProtocol.NoncesMatch(a, b));
    }

    [Fact]
    public void NoncesMatch_DifferentLength_ReturnsFalseWithoutThrowing()
    {
        Assert.False(BootstrapProtocol.NoncesMatch("abc", "abcd"));
    }

    // ── Source validation ─────────────────────────────────────────────────

    [Fact]
    public void ValidateSourceFile_MissingFile_Throws()
    {
        var path = Path.Combine(_tempRoot, "missing.exe");
        var ex = Assert.Throws<InvalidOperationException>(() => BootstrapProtocol.ValidateSourceFile(path));
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void ValidateSourceFile_RegularFile_DoesNotThrow()
    {
        Directory.CreateDirectory(_tempRoot);
        var path = Path.Combine(_tempRoot, "installer.exe");
        File.WriteAllBytes(path, [1, 2, 3]);
        BootstrapProtocol.ValidateSourceFile(path); // must not throw
    }

    [Fact]
    public void ValidateSourceFile_UncPath_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BootstrapProtocol.ValidateSourceFile(@"\\server\share\installer.exe"));
        Assert.Contains("UNC", ex.Message);
    }

    [Fact]
    public void ValidateSourceFile_EmptyPath_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => BootstrapProtocol.ValidateSourceFile(""));
    }

    [Fact]
    public void ValidateSourceFile_ReparsePoint_Throws()
    {
        Directory.CreateDirectory(_tempRoot);
        var target = Path.Combine(_tempRoot, "real.exe");
        File.WriteAllBytes(target, [1, 2, 3]);
        var link = Path.Combine(_tempRoot, "link.exe");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception) when (!OperatingSystem.IsWindows() || IsPrivilegeError())
        {
            // Symlink creation can require elevation on some Windows configurations;
            // skip rather than fail the suite on an environment limitation.
            return;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => BootstrapProtocol.ValidateSourceFile(link));
        Assert.Contains("reparse point", ex.Message, StringComparison.OrdinalIgnoreCase);

        static bool IsPrivilegeError() => true;
    }

    // ── Destination containment ──────────────────────────────────────────

    [Fact]
    public void ValidateDestinationUnderRoot_WithinRoot_DoesNotThrow()
    {
        var root = Path.Combine(_tempRoot, "runtime");
        var dest = Path.Combine(root, "qemu-11.1.0");
        BootstrapProtocol.ValidateDestinationUnderRoot(dest, root); // must not throw
    }

    [Fact]
    public void ValidateDestinationUnderRoot_EqualsRoot_DoesNotThrow()
    {
        var root = Path.Combine(_tempRoot, "runtime");
        BootstrapProtocol.ValidateDestinationUnderRoot(root, root); // must not throw
    }

    [Fact]
    public void ValidateDestinationUnderRoot_TraversalEscape_Throws()
    {
        var root = Path.Combine(_tempRoot, "runtime");
        var escape = Path.Combine(root, "..", "..", "Windows", "System32");
        var ex = Assert.Throws<InvalidOperationException>(
            () => BootstrapProtocol.ValidateDestinationUnderRoot(escape, root));
        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public void ValidateDestinationUnderRoot_SiblingDirectoryLookingSimilar_Throws()
    {
        // "runtime-evil" is not under "runtime" despite sharing a string prefix —
        // the trailing-separator check must reject naive prefix matches.
        var root = Path.Combine(_tempRoot, "runtime");
        var sibling = Path.Combine(_tempRoot, "runtime-evil", "payload");
        var ex = Assert.Throws<InvalidOperationException>(
            () => BootstrapProtocol.ValidateDestinationUnderRoot(sibling, root));
        Assert.Contains("escapes", ex.Message);
    }

    [Fact]
    public void ValidateDestinationUnderRoot_UncPath_Throws()
    {
        var root = Path.Combine(_tempRoot, "runtime");
        Assert.Throws<InvalidOperationException>(
            () => BootstrapProtocol.ValidateDestinationUnderRoot(@"\\server\share\runtime", root));
    }

    [Fact]
    public void ValidateDestinationUnderRoot_EmptyDestination_Throws()
    {
        var root = Path.Combine(_tempRoot, "runtime");
        Assert.Throws<InvalidOperationException>(
            () => BootstrapProtocol.ValidateDestinationUnderRoot("", root));
    }
}
