using Serpy.Core.Downloads;

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

    /// <summary>Every site the pinned input list needs, whether or not a part of it is already on this PC.</summary>
    public static IReadOnlyList<Uri> For(BuildInputs inputs)
    {
        var sites = new List<Uri>();
        if (inputs.Parts is null) return sites;

        foreach (var part in inputs.Parts)
        {
            if (part.Files is null) continue;
            foreach (var file in part.Files)
            {
                if (file.Urls is null) continue;
                foreach (var url in file.Urls)
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    {
                        sites.Add(new Uri($"https://{uri.Host}/"));
                    }
                }
            }
        }

        var hasPlaceholderParts = inputs.Parts.Any(p => p.Files.Count == 0 &&
            !string.Equals(p.Id, "engine", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(p.Id, "os", StringComparison.OrdinalIgnoreCase));

        if (hasPlaceholderParts)
        {
            foreach (var site in BuildSites)
            {
                sites.Add(new Uri($"https://{site}/"));
            }
        }

        return sites.DistinctBy(u => u.Host, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The sites to ask for this run from the pinned input list.
    /// Returns empty when an accepted system image exists or when every file is already cached.
    /// </summary>
    public static IReadOnlyList<Uri> StillNeeded(BuildInputs inputs, string applianceDirectory, DownloadCache? cache = null)
    {
        var image = Path.Combine(applianceDirectory, "system.qcow2");
        var accepted = File.Exists(image) && File.Exists(Path.Combine(applianceDirectory, SystemImageManifest.FileName));
        if (accepted) return [];

        if (cache is not null && inputs.Parts is not null)
        {
            var allCached = true;
            foreach (var part in inputs.Parts)
            {
                if (part.Files is null) continue;
                foreach (var file in part.Files)
                {
                    if (!cache.TryVerify(file))
                    {
                        allCached = false;
                        break;
                    }
                }
                if (!allCached) break;
            }

            if (allCached) return [];
        }

        return For(inputs);
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
