using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Versions;

public sealed class VersionManifestLoaderTests
{
    [Fact]
    public void Load_RealVersionsYaml_ParsesRuntimeLocks()
    {
        // Walks up from the test output directory to find config/versions.yaml.
        var manifest = VersionManifestLoader.Load();

        Assert.NotEmpty(manifest.Qemu.Version);
        Assert.NotEmpty(manifest.Runtime.Python.Lock);
        Assert.NotEmpty(manifest.Runtime.Python.Floor);
        Assert.NotEmpty(manifest.Runtime.Node.Lock);
        Assert.NotEmpty(manifest.Runtime.MariaDb.Lock);
        Assert.NotEmpty(manifest.Apps.Frappe.Branch);
        Assert.NotEmpty(manifest.Apps.Frappe.Lock);
        Assert.NotEmpty(manifest.Apps.ErpNext.Branch);
        Assert.NotEmpty(manifest.Apps.ErpNext.Lock);
    }

    [Fact]
    public void Load_RealVersionsYaml_PinsExactApplicationCommits()
    {
        var manifest = VersionManifestLoader.Load();

        Assert.Matches("^[0-9a-f]{40}$", manifest.Apps.Frappe.Commit);
        Assert.Matches("^[0-9a-f]{40}$", manifest.Apps.ErpNext.Commit);
    }

    [Fact]
    public void Load_RealVersionsYaml_LocksAreValidVersionStrings()
    {
        var manifest = VersionManifestLoader.Load();

        // Locks must be parseable as version tuples (no exception from Compare).
        Assert.True(VersionEvaluator.MeetsFloor(
            manifest.Runtime.Python.Lock, manifest.Runtime.Python.Floor),
            "Python lock must meet its own floor");
        Assert.True(VersionEvaluator.MeetsFloor(
            manifest.Runtime.Node.Lock, manifest.Runtime.Node.Floor),
            "Node lock must meet its own floor");
    }

    [Fact]
    public void LoadFrom_InlineYaml_ParsesAllSections()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"test-versions-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(tmp, """
            qemu:
              version: "11.1.0"
              windows:
                archiveUrl: "https://example.com/qemu.zip"
                archiveSha256: "def789"
                installerUrl: "https://example.com/qemu.exe"
                installerSha256: "abc123"
                sourceUrl: "https://example.com/src"
                licenseNoticeUrl: "https://example.com/license"
            debianCloudImage:
              version: "20260819-2575"
              url: "https://example.com/debian.qcow2"
              sha512: "abc512"
              release: "trixie"
              arch: "amd64"
            runtime:
              python:
                lock: "3.14.7"
                floor: "3.14.0"
              node:
                lock: "24.2.0"
                floor: "24.0.0"
              mariadb:
                lock: "11.8.3"
                floor: "11.8.0"
              redis:
                lock: "8.0.2"
                floor: "8.0.0"
            apps:
              frappe:
                branch: "v16.31.0"
                commit: "6a329d068416768ec47ccd3326b9cc95a8d7bf99"
                lock: "16.31.0"
                minVersion: "16.0.0"
              erpnext:
                branch: "v16.32.3"
                commit: "11e0ba0a1c45f217e2e73e885f699102d06da325"
                lock: "16.32.3"
                minVersion: "16.0.0"
            """);

        try
        {
            var m = VersionManifestLoader.LoadFrom(tmp);
            Assert.Equal("11.1.0",                       m.Qemu.Version);
            Assert.Equal("https://example.com/qemu.zip", m.Qemu.Windows.ArchiveUrl);
            Assert.Equal("def789", m.Qemu.Windows.ArchiveSha256);
            Assert.Equal("https://example.com/qemu.exe", m.Qemu.Windows.InstallerUrl);
            Assert.Equal("abc123", m.Qemu.Windows.InstallerSha256);
            Assert.Equal("trixie",             m.DebianCloudImage.Release);
            Assert.Equal("6a329d068416768ec47ccd3326b9cc95a8d7bf99", m.Apps.Frappe.Commit);
            Assert.Equal("11e0ba0a1c45f217e2e73e885f699102d06da325", m.Apps.ErpNext.Commit);
            Assert.Equal("abc512",             m.DebianCloudImage.Sha512);
            Assert.Equal("3.14.7",             m.Runtime.Python.Lock);
            Assert.Equal("24.0.0",             m.Runtime.Node.Floor);
            Assert.Equal("11.8.3",             m.Runtime.MariaDb.Lock);
            Assert.Equal("8.0.0",              m.Runtime.Redis.Floor);
            Assert.Equal("v16.31.0",           m.Apps.Frappe.Branch);
            Assert.Equal("16.31.0",            m.Apps.Frappe.Lock);
            Assert.Equal("v16.32.3",           m.Apps.ErpNext.Branch);
            Assert.Equal("16.32.3",            m.Apps.ErpNext.Lock);
            Assert.Equal("16.0.0",             m.Apps.ErpNext.MinVersion);
        }
        finally { File.Delete(tmp); }
    }
}
