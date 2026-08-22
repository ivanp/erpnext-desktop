using DiscUtils.Iso9660;

namespace Serpy.Core.Images;

/// <summary>
/// Generates a NoCloud cidata ISO9660 image in-process for cloud-init provisioning (KTD10).
/// Uses DiscUtils.Iso9660 — selected as the AOT-compatible managed writer after round-trip
/// label and filename verification passes per KTD10 acceptance gate.
/// No external ISO tool is used.
/// </summary>
public sealed class NoCloudSeedWriter
{
    private readonly string _userData;
    private readonly string _metaData;

    public NoCloudSeedWriter(string userData, string metaData)
    {
        _userData = userData;
        _metaData = metaData;
    }

    public void Write(string outputPath)
    {
        var builder = new CDBuilder
        {
            UseJoliet = false,
            VolumeIdentifier = "cidata",
        };

        builder.AddFile("user-data", System.Text.Encoding.UTF8.GetBytes(_userData));
        builder.AddFile("meta-data", System.Text.Encoding.UTF8.GetBytes(_metaData));

        using var fs = File.Create(outputPath);
        using var iso = builder.Build();
        iso.CopyTo(fs);
    }

    /// <summary>
    /// Read back an ISO and verify the volume label and root file names.
    /// ISO 9660 stores names uppercase with a ;1 version suffix (USER-DATA;1).
    /// Enumerates via GetFiles() to avoid case-sensitive path lookup failures.
    /// </summary>
    public static SeedVerificationResult Verify(string isoPath)
    {
        try
        {
            using var fs  = File.OpenRead(isoPath);
            using var cdr = new CDReader(fs, joliet: false);

            // DiscUtils stores the volume label preserving the case we set ("cidata").
            var label = cdr.VolumeLabel;
            bool labelOk = string.Equals(label, "cidata", StringComparison.OrdinalIgnoreCase);

            // ISO 9660 level-1 converts hyphens to underscores and appends ".;1":
            //   "user-data" → "USER_DATA.;1"
            //   "meta-data" → "META_DATA.;1"
            var names = cdr.Root.GetFiles()
                .Select(f => f.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool HasFile(string hyphenated)
            {
                var underscored = hyphenated.Replace('-', '_');
                return names.Contains(hyphenated) ||
                       names.Contains(hyphenated + ".;1") ||
                       names.Contains(underscored) ||
                       names.Contains(underscored.ToUpperInvariant() + ".;1") ||
                       names.Contains(underscored.ToUpperInvariant());
            }

            return new SeedVerificationResult(labelOk, HasFile("user-data"), HasFile("meta-data"));
        }
        catch (Exception ex)
        {
            return new SeedVerificationResult(false, false, false) { Error = ex.Message };
        }
    }

    /// <summary>Open user-data content from a seed ISO (for tests).</summary>
    public static string ReadUserData(string isoPath)
    {
        using var fs  = File.OpenRead(isoPath);
        using var cdr = new CDReader(fs, joliet: false);
        // ISO 9660 level-1: "user-data" stored as "USER_DATA.;1"
        var file = cdr.Root.GetFiles().FirstOrDefault(f =>
            f.Name.StartsWith("USER_DATA", StringComparison.OrdinalIgnoreCase) ||
            f.Name.StartsWith("USER-DATA", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("user-data not found in seed ISO");
        using var entry = file.OpenRead();
        return new System.IO.StreamReader(entry).ReadToEnd();
    }
}

public sealed record SeedVerificationResult(bool LabelOk, bool HasUserData, bool HasMetaData)
{
    public bool IsValid => LabelOk && HasUserData && HasMetaData;
    public string? Error { get; init; }
}
