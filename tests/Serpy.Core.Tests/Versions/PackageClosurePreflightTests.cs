using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Versions;

public sealed class PackageClosurePreflightTests
{
    [Fact]
    public async Task ValidateAsync_AllLockedArtifactsResolve_Succeeds()
    {
        var manifest = Manifest();
        using var handler = new FixtureHandler(new Dictionary<string, string>
        {
            ["trixie/main/binary-amd64/Packages.gz"] = AptPackages(("redis-server", "5:8.0.2-3+deb13u2")),
            ["sid/main/binary-amd64/Packages.gz"] = AptPackages(
                ("python3.14", "3.14.7-1"),
                ("python3.14-dev", "3.14.7-1"),
                ("python3.14-venv", "3.14.7-1")),
            ["mariadb-11.8.3/repo/debian/dists/trixie/main/binary-amd64/Packages.gz"] = AptPackages(
                ("mariadb-server", "1:11.8.3+maria~deb13")),
            ["node-v24.2.0-linux-x64.tar.xz"] = "node archive",
            ["v24.2.0/SHASUMS256.txt"] =
                "91a0794f4dbc94bc4a9296139ed9101de21234982bae2b325e37ebd3462273e5  node-v24.2.0-linux-x64.tar.xz\n",
            ["git/ref/tags/v16.31.0"] = "{\"ref\":\"refs/tags/v16.31.0\"}",
            ["git/ref/tags/v16.32.3"] = "{\"ref\":\"refs/tags/v16.32.3\"}",
            ["pypi/frappe-bench/5.31.0/json"] = "{\"info\":{\"version\":\"5.31.0\"}}",
        });

        var preflight = new PackageClosurePreflight(manifest, handler);

        await preflight.ValidateAsync();

        Assert.Contains(handler.Requests, uri => uri.Contains("trixie/main/binary-amd64/Packages.gz", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, uri => uri.Contains("node-v24.2.0-linux-x64.tar.xz", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, uri => uri.Contains("pypi/frappe-bench/5.31.0/json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateAsync_MissingLockedPackage_ReportsHostResolutionFailure()
    {
        using var handler = new FixtureHandler(new Dictionary<string, string>
        {
            ["trixie/main/binary-amd64/Packages.gz"] = AptPackages(("redis-server", "5:8.0.1-1")),
        });
        var preflight = new PackageClosurePreflight(Manifest(), handler);

        var ex = await Assert.ThrowsAsync<PackageClosureException>(() => preflight.ValidateAsync());

        Assert.Contains("host-side package-closure preflight", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redis-server", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8.0.2", ex.Message, StringComparison.Ordinal);
    }

    private static VersionManifest Manifest() => new()
    {
        Runtime = new VersionManifest.RuntimeSection
        {
            Python = new VersionManifest.LockFloor { Lock = "3.14.7", Floor = "3.14.0" },
            Node = new VersionManifest.LockFloor { Lock = "24.2.0", Floor = "24.0.0" },
            MariaDb = new VersionManifest.LockFloor { Lock = "11.8.3", Floor = "11.8.0" },
            Redis = new VersionManifest.LockFloor { Lock = "8.0.2", Floor = "8.0.0" },
        },
        Apps = new VersionManifest.AppsSection
        {
            Frappe = new VersionManifest.AppEntry { Branch = "v16.31.0", Lock = "16.31.0", MinVersion = "16.0.0" },
            ErpNext = new VersionManifest.AppEntry { Branch = "v16.32.3", Lock = "16.32.3", MinVersion = "16.0.0" },
        },
    };

    private static string AptPackages(params (string Name, string Version)[] packages) => string.Join("\n\n", packages.Select(p => $"Package: {p.Name}\nVersion: {p.Version}"));

    private sealed class FixtureHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            var pair = responses
                .OrderByDescending(entry => entry.Key.Length)
                .FirstOrDefault(entry => uri.EndsWith(entry.Key, StringComparison.Ordinal));
            if (pair.Key is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            HttpContent content = pair.Key.EndsWith(".gz", StringComparison.Ordinal)
                ? Gzip(pair.Value)
                : new ByteArrayContent(Encoding.UTF8.GetBytes(pair.Value));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private static ByteArrayContent Gzip(string value)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            using (var input = new StreamWriter(gzip, Encoding.UTF8, leaveOpen: true))
                input.Write(value);
            return new ByteArrayContent(output.ToArray());
        }
    }
}
