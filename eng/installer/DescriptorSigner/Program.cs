using System.Security.Cryptography;
using System.Text.Json;
using Serpy.Core.Qemu;
using Serpy.Core.Versions;

var parsed = ParseArgs(args);

var versionsPath = parsed.GetValueOrDefault("versions") ?? FindRepoFile("config/versions.yaml");
if (string.IsNullOrEmpty(versionsPath) || !File.Exists(versionsPath))
{
    Console.Error.WriteLine($"Error: versions.yaml not found at '{versionsPath}'.");
    return 1;
}

var keyPath = parsed.GetValueOrDefault("key") ?? FindRepoFile(".local-signing/descriptor-signing-private.pem");
if (string.IsNullOrEmpty(keyPath) || !File.Exists(keyPath))
{
    Console.Error.WriteLine(
        $"Error: Private key not found at '{keyPath}'. " +
        "For local development, generate one using: ./eng/gen-descriptor-signing-key.sh");
    return 1;
}

var outputPath = parsed.GetValueOrDefault("out") ?? "dist/runtime-descriptor.signed.json";

Console.WriteLine($"Loading versions from: {versionsPath}");
var manifest = VersionManifestLoader.LoadFrom(versionsPath);

if (string.IsNullOrWhiteSpace(manifest.Qemu.Version))
    throw new InvalidOperationException("qemu.version in versions.yaml is empty.");
if (string.IsNullOrWhiteSpace(manifest.Qemu.Windows.InstallerUrl))
    throw new InvalidOperationException("qemu.windows.installerUrl in versions.yaml is empty.");
if (string.IsNullOrWhiteSpace(manifest.Qemu.Windows.InstallerSha256))
    throw new InvalidOperationException("qemu.windows.installerSha256 in versions.yaml is empty.");

var descriptor = new RuntimeDescriptor(
    QemuVersion: manifest.Qemu.Version,
    InstallerUrl: manifest.Qemu.Windows.InstallerUrl,
    InstallerSha256: manifest.Qemu.Windows.InstallerSha256,
    SourceUrl: manifest.Qemu.Windows.SourceUrl,
    LicenseNoticeUrl: manifest.Qemu.Windows.LicenseNoticeUrl);

Console.WriteLine($"Signing descriptor for QEMU v{descriptor.QemuVersion} (SHA-256: {descriptor.InstallerSha256})...");
var keyPem = await File.ReadAllTextAsync(keyPath);

using var rsa = RSA.Create();
rsa.ImportFromPem(keyPem);

var signature = DescriptorSigner.Sign(descriptor, rsa);
var signedFile = new SignedRuntimeDescriptorFile(descriptor, Convert.ToBase64String(signature));
var signedDescriptor = signedFile.ToSignedRuntimeDescriptor();

// Verify mathematically against the public key corresponding to this private key
var ownPublicKeySpki = rsa.ExportSubjectPublicKeyInfo();
if (!DescriptorVerifier.TryVerify(signedDescriptor, ownPublicKeySpki))
{
    Console.Error.WriteLine("Error: Self-verification against exported public key failed.");
    return 2;
}

if (parsed.ContainsKey("verify"))
{
    try
    {
        var verified = DescriptorVerifier.VerifyOrThrow(signedDescriptor);
        Console.WriteLine($"Verified against compiled-in trust anchor: QEMU v{verified.QemuVersion}.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Verification failed against compiled-in trust anchor: {ex.Message}");
        return 3;
    }
}

var json = JsonSerializer.Serialize(signedFile, SignedRuntimeDescriptorFileJsonContext.Default.SignedRuntimeDescriptorFile);

var fullOut = Path.GetFullPath(outputPath);
var parentDir = Path.GetDirectoryName(fullOut);
if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
{
    Directory.CreateDirectory(parentDir);
}

await File.WriteAllTextAsync(fullOut, json);
Console.WriteLine($"Created signed runtime descriptor: {fullOut}");
return 0;

static string? FindRepoFile(string relativePath)
{
    var current = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && current is not null; i++, current = Path.GetDirectoryName(current))
    {
        var candidate = Path.Combine(current, relativePath);
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
    }
    return null;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg is "-v" or "--versions")
        {
            if (i + 1 < args.Length) map["versions"] = args[++i];
        }
        else if (arg is "-k" or "--key")
        {
            if (i + 1 < args.Length) map["key"] = args[++i];
        }
        else if (arg is "-o" or "--out")
        {
            if (i + 1 < args.Length) map["out"] = args[++i];
        }
        else if (arg is "--verify")
        {
            map["verify"] = "true";
        }
    }
    return map;
}
