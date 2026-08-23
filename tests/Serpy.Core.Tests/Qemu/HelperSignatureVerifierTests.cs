using System.Runtime.Versioning;
using Serpy.Core.Qemu;

namespace Serpy.Core.Tests.Qemu;

[SupportedOSPlatform("windows")]
public sealed class HelperSignatureVerifierTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"SerpySigTest-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Verify_MissingFile_ReturnsInvalidWithoutThrowing()
    {
        var path = Path.Combine(_tempRoot, "missing.exe");
        var result = HelperSignatureVerifier.Verify(path, "CN=Serpy Test Publisher, O=Serpy Dev, C=US");

        Assert.False(result.IsValid);
        Assert.False(result.ChainIsTrusted);
        Assert.Contains("does not exist", result.Message);
    }

    [Fact]
    public void Verify_GenuinelyUnsignedFile_ReturnsChainNotTrustedNoSubjectExtracted()
    {
        // A plain non-PE file has no Authenticode signature to find at all.
        Directory.CreateDirectory(_tempRoot);
        var path = Path.Combine(_tempRoot, "not-a-binary.exe");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        var result = HelperSignatureVerifier.Verify(path, "CN=Serpy Test Publisher, O=Serpy Dev, C=US");

        Assert.False(result.IsValid);
        Assert.False(result.ChainIsTrusted);
        Assert.False(result.SubjectMatches);
        Assert.Null(result.ActualSubject);
    }

    [Fact]
    public void Verify_RealUnsignedDotnetExecutable_ReportsNotSigned()
    {
        // A genuine, freshly-built managed executable that was never Authenticode
        // signed -- exercises the real WinVerifyTrust call against a valid PE that
        // legitimately carries no signature (distinct from the "not a PE at all"
        // case above, which WinVerifyTrust may reject for a different reason).
        var thisTestAssembly = typeof(HelperSignatureVerifierTests).Assembly.Location;
        var unsignedDll = Path.ChangeExtension(thisTestAssembly, ".dll");
        if (!File.Exists(unsignedDll))
            return; // environment without an accessible on-disk managed assembly -- skip

        var result = HelperSignatureVerifier.Verify(unsignedDll, "CN=Serpy Test Publisher, O=Serpy Dev, C=US");

        Assert.False(result.ChainIsTrusted);
        Assert.False(result.SubjectMatches);
    }
}
