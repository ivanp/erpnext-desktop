using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Serpy.Core.Versions;

/// <summary>
/// Resolves every immutable guest input before the expensive provisioning boot.
/// This is host-side evidence: a failure means the pinned dependency disappeared
/// or changed upstream, not that cloud-init failed inside the guest.
/// </summary>
public sealed class PackageClosurePreflight(VersionManifest manifest, HttpMessageHandler? httpHandler = null)
{
    private const string DebianSnapshot = "https://snapshot.debian.org/archive/debian/20260822T000000Z";
    private const string MariaDbArchive = "https://archive.mariadb.org/mariadb-11.8.3/repo/debian";
    private const string NodeBase = "https://nodejs.org/dist";
    private const string GitHubApi = "https://api.github.com/repos";
    private const string PyPi = "https://pypi.org/pypi";
    private const string NodeLinuxX64Sha256 = "91a0794f4dbc94bc4a9296139ed9101de21234982bae2b325e37ebd3462273e5";

    public async Task ValidateAsync(CancellationToken ct = default)
    {
        using var http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Serpy-PackageClosurePreflight/1.0");

        try
        {
            await AssertAptPackageAsync(http, $"{DebianSnapshot}/dists/trixie/main/binary-amd64/Packages.gz",
                "redis-server", $"5:{manifest.Runtime.Redis.Lock}-3+deb13u2", ct);
            await AssertAptPackageAsync(http, $"{DebianSnapshot}/dists/sid/main/binary-amd64/Packages.gz",
                "python3.14", $"{manifest.Runtime.Python.Lock}-1", ct);
            await AssertAptPackageAsync(http, $"{DebianSnapshot}/dists/sid/main/binary-amd64/Packages.gz",
                "python3.14-dev", $"{manifest.Runtime.Python.Lock}-1", ct);
            await AssertAptPackageAsync(http, $"{DebianSnapshot}/dists/sid/main/binary-amd64/Packages.gz",
                "python3.14-venv", $"{manifest.Runtime.Python.Lock}-1", ct);
            await AssertAptPackageAsync(http, $"{MariaDbArchive}/dists/trixie/main/binary-amd64/Packages.gz",
                "mariadb-server", $"1:{manifest.Runtime.MariaDb.Lock}+maria~deb13", ct);

            var nodeVersion = manifest.Runtime.Node.Lock;
            await AssertSuccessAsync(http, $"{NodeBase}/v{nodeVersion}/node-v{nodeVersion}-linux-x64.tar.xz", ct);
            var sums = await GetTextAsync(http, $"{NodeBase}/v{nodeVersion}/SHASUMS256.txt", ct);
            var expectedNodeLine = $"{NodeLinuxX64Sha256}  node-v{nodeVersion}-linux-x64.tar.xz";
            if (!sums.Contains(expectedNodeLine, StringComparison.Ordinal))
                throw new InvalidOperationException($"Node SHA-256 manifest lacks '{expectedNodeLine}'.");

            await AssertGitTagAsync(http, "frappe/frappe", manifest.Apps.Frappe.Branch, ct);
            await AssertGitTagAsync(http, "frappe/erpnext", manifest.Apps.ErpNext.Branch, ct);
            await AssertPyPiVersionAsync(http, "frappe-bench", "5.31.0", ct);
        }
        catch (PackageClosureException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            throw new PackageClosureException($"Host-side package-closure preflight failed: {ex.Message}", ex);
        }
    }

    private static async Task AssertAptPackageAsync(HttpClient http, string url, string package, string version, CancellationToken ct)
    {
        using var compressed = await http.GetStreamAsync(url, ct);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var packages = await reader.ReadToEndAsync(ct);
        var entry = packages.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(block => block.Split('\n').Any(line => string.Equals(line.Trim(), $"Package: {package}", StringComparison.Ordinal)) &&
                                     block.Split('\n').Any(line => string.Equals(line.Trim(), $"Version: {version}", StringComparison.Ordinal)));
        if (entry is null)
            throw new InvalidOperationException($"APT snapshot does not resolve {package}={version} from {url}.");
    }

    private static async Task AssertGitTagAsync(HttpClient http, string repository, string tag, CancellationToken ct)
    {
        await AssertSuccessAsync(http, $"{GitHubApi}/{repository}/git/ref/tags/{tag}", ct);
    }

    private static async Task AssertPyPiVersionAsync(HttpClient http, string package, string version, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetTextAsync(http, $"{PyPi}/{package}/{version}/json", ct));
        var resolved = doc.RootElement.GetProperty("info").GetProperty("version").GetString();
        if (!string.Equals(resolved, version, StringComparison.Ordinal))
            throw new InvalidOperationException($"PyPI resolved {package}={resolved}; expected {version}.");
    }

    private static async Task AssertSuccessAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> GetTextAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}

public sealed class PackageClosureException(string message, Exception? inner = null) : Exception(message, inner);
