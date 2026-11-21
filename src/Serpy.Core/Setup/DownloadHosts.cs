using Serpy.Core.Images;
using Serpy.Core.Versions;

namespace Serpy.Core.Setup;

/// <summary>
/// The download sites the PC check sends one request to (KTD13). It lists the sites that a build
/// contacts today: the sites that the host-side package-closure preflight reads, the site of the
/// Debian image and the key site that the guest script reads. The Pinned Input List (KTD25)
/// replaces this list with the sites of its files.
/// </summary>
public static class DownloadHosts
{
    private static readonly string[] BuildSites =
    [
        "snapshot.debian.org",
        "archive.mariadb.org",
        "supplychain.mariadb.com",
        "nodejs.org",
        "api.github.com",
        "pypi.org",
        "registry.npmjs.org",
    ];

    /// <summary>Every site a build needs, whether or not a part of it is already on this PC.</summary>
    public static IReadOnlyList<Uri> For(VersionManifest manifest)
    {
        var sites = new List<Uri>();
        if (Uri.TryCreate(manifest.DebianCloudImage.Url, UriKind.Absolute, out var image) &&
            image.Scheme == Uri.UriSchemeHttps)
            sites.Add(new Uri($"https://{image.Host}/"));

        sites.AddRange(BuildSites.Select(host => new Uri($"https://{host}/")));
        return sites.DistinctBy(u => u.Host, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The sites to ask for this run. An accepted system image means the build has nothing left to
    /// download, so no site is asked and the check does not fail an offline PC for nothing. The test
    /// is the image and its manifest being present; the Build step still validates the digest.
    /// </summary>
    public static IReadOnlyList<Uri> StillNeeded(VersionManifest manifest, string applianceDirectory)
    {
        var image = Path.Combine(applianceDirectory, "system.qcow2");
        var accepted = File.Exists(image) && File.Exists(Path.Combine(applianceDirectory, SystemImageManifest.FileName));
        return accepted ? [] : For(manifest);
    }
}
