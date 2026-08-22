using System.Text;

namespace Serpy.Core.Images;

/// <summary>
/// Generates a minimal ISO 9660 NoCloud seed in process. The implementation
/// owns the two-file ISO layout rather than depending on reflective filesystem
/// discovery, keeping Windows Native-AOT a hard acceptance gate.
/// </summary>
public sealed class NoCloudSeedWriter
{
    private const int SectorSize = 2048;
    private const int PrimaryVolumeDescriptorSector = 16;
    private const int RootDirectorySector = 20;
    private const int FirstFileSector = 21;

    private readonly string _userData;
    private readonly string _metaData;

    public NoCloudSeedWriter(string userData, string metaData)
    {
        _userData = userData;
        _metaData = metaData;
    }

    /// <summary>Renders canonical guest helpers into the cloud-init template.</summary>
    public static string RenderUserData(
        string template,
        string provisionDoneScript,
        string initDataScript,
        string recoverScript) =>
        ReplaceToken(
            ReplaceToken(
                ReplaceToken(template, "@@SERPY_PROVISION_DONE@@", provisionDoneScript),
                "@@SERPY_INIT_DATA@@", initDataScript),
            "@@SERPY_RECOVER@@", recoverScript);

    /// <summary>Parse the selected cloud-init <c>content: |</c> block.</summary>
    public static string? ExtractWriteFileContent(string renderedUserData, string guestPath)
    {
        var lines = renderedUserData.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var targetFound = false;
        var inContent = false;
        var blockIndent = -1;
        var body = new StringBuilder();

        foreach (var raw in lines)
        {
            var trimmed = raw.TrimStart();
            if (!targetFound)
            {
                if (trimmed.StartsWith("- path:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("path:", StringComparison.Ordinal))
                {
                    var value = trimmed[(trimmed.IndexOf(':') + 1)..].Trim().Trim('"', '\'');
                    targetFound = string.Equals(value, guestPath, StringComparison.Ordinal);
                }
                continue;
            }

            if (!inContent)
            {
                if (trimmed.StartsWith("content: |", StringComparison.Ordinal))
                    inContent = true;
                else if (trimmed.StartsWith("- path:", StringComparison.Ordinal))
                    return null;
                continue;
            }

            if (trimmed.Length == 0)
            {
                body.Append('\n');
                continue;
            }

            var indent = raw.Length - trimmed.Length;
            if (blockIndent < 0) blockIndent = indent;
            if (indent < blockIndent) break;
            body.Append(raw[blockIndent..]).Append('\n');
        }

        return inContent ? body.ToString().TrimEnd('\n', '\r', ' ') + "\n" : null;
    }

    /// <summary>Write a standards-conforming ISO 9660 seed with two files.</summary>
    public void Write(string outputPath)
    {
        var userData = Encoding.UTF8.GetBytes(_userData);
        var metaData = Encoding.UTF8.GetBytes(_metaData);
        var userSector = FirstFileSector;
        var metaSector = userSector + SectorsFor(userData.Length);
        var totalSectors = metaSector + SectorsFor(metaData.Length);
        var image = new byte[checked(totalSectors * SectorSize)];

        WriteVolumeDescriptor(image, totalSectors);
        WriteTerminator(image);
        WritePathTable(image);
        WriteRootDirectory(image, userSector, userData.Length, metaSector, metaData.Length);
        Buffer.BlockCopy(userData, 0, image, userSector * SectorSize, userData.Length);
        Buffer.BlockCopy(metaData, 0, image, metaSector * SectorSize, metaData.Length);
        File.WriteAllBytes(outputPath, image);
    }

    /// <summary>Read back label and expected root entries without reflection.</summary>
    public static SeedVerificationResult Verify(string isoPath)
    {
        try
        {
            var image = File.ReadAllBytes(isoPath);
            RequireSector(image, PrimaryVolumeDescriptorSector);
            var pvd = PrimaryVolumeDescriptorSector * SectorSize;
            if (image[pvd] != 1 || Encoding.ASCII.GetString(image, pvd + 1, 5) != "CD001")
                return new(false, false, false) { Error = "ISO primary volume descriptor not found." };

            var label = Encoding.ASCII.GetString(image, pvd + 40, 32).TrimEnd(' ');
            var root = ReadDirectory(image, RootDirectorySector);
            return new(
                string.Equals(label, "cidata", StringComparison.OrdinalIgnoreCase),
                root.Any(e => e.Name == "USER-DATA;1"),
                root.Any(e => e.Name == "META-DATA;1"));
        }
        catch (Exception ex)
        {
            return new(false, false, false) { Error = ex.Message };
        }
    }

    /// <summary>Open user-data content from a Serpy seed ISO.</summary>
    public static string ReadUserData(string isoPath)
    {
        var image = File.ReadAllBytes(isoPath);
        var entry = ReadDirectory(image, RootDirectorySector)
            .SingleOrDefault(e => e.Name == "USER-DATA;1");
        if (entry == default)
            throw new FileNotFoundException("user-data not found in seed ISO", isoPath);
        return Encoding.UTF8.GetString(image, entry.Sector * SectorSize, entry.Length);
    }

    private static void WriteVolumeDescriptor(byte[] image, int totalSectors)
    {
        var offset = PrimaryVolumeDescriptorSector * SectorSize;
        image[offset] = 1;
        WriteAscii(image, offset + 1, "CD001");
        image[offset + 6] = 1;
        WriteAscii(image, offset + 8, "SERPY", 32);
        WriteAscii(image, offset + 40, "cidata", 32);
        WriteBothEndianInt32(image, offset + 80, totalSectors);
        WriteBothEndianInt16(image, offset + 120, 1);
        WriteBothEndianInt16(image, offset + 124, 1);
        WriteBothEndianInt16(image, offset + 128, SectorSize);
        WriteBothEndianInt32(image, offset + 132, 10);
        WriteInt32LittleEndian(image, offset + 140, 18); // Type-L path table
        WriteInt32BigEndian(image, offset + 144, 18);
        WriteInt32LittleEndian(image, offset + 148, 19); // Type-M path table
        WriteInt32BigEndian(image, offset + 152, 19);
        WriteDirectoryRecord(image, offset + 156, RootDirectorySector, SectorSize, [0], true);
    }

    private static void WriteTerminator(byte[] image)
    {
        var offset = (PrimaryVolumeDescriptorSector + 1) * SectorSize;
        image[offset] = 255;
        WriteAscii(image, offset + 1, "CD001");
        image[offset + 6] = 1;
    }

    private static void WritePathTable(byte[] image)
    {
        WritePathTableEntry(image, 18 * SectorSize, RootDirectorySector, false);
        WritePathTableEntry(image, 19 * SectorSize, RootDirectorySector, true);
    }

    private static void WritePathTableEntry(byte[] image, int offset, int rootSector, bool bigEndian)
    {
        image[offset] = 1;
        image[offset + 1] = 0;
        if (bigEndian)
        {
            WriteInt32BigEndian(image, offset + 2, rootSector);
            WriteInt16BigEndian(image, offset + 6, 1);
        }
        else
        {
            WriteInt32LittleEndian(image, offset + 2, rootSector);
            WriteInt16LittleEndian(image, offset + 6, 1);
        }
        image[offset + 8] = 0;
    }

    private static void WriteRootDirectory(byte[] image, int userSector, int userLength, int metaSector, int metaLength)
    {
        var offset = RootDirectorySector * SectorSize;
        offset += WriteDirectoryRecord(image, offset, RootDirectorySector, SectorSize, [0], true);
        offset += WriteDirectoryRecord(image, offset, RootDirectorySector, SectorSize, [1], true);
        offset += WriteDirectoryRecord(image, offset, userSector, userLength, Encoding.ASCII.GetBytes("USER-DATA;1"), false);
        WriteDirectoryRecord(image, offset, metaSector, metaLength, Encoding.ASCII.GetBytes("META-DATA;1"), false);
    }

    private static int WriteDirectoryRecord(byte[] image, int offset, int sector, int length, byte[] identifier, bool isDirectory)
    {
        var recordLength = 33 + identifier.Length + (identifier.Length % 2 == 0 ? 1 : 0);
        image[offset] = checked((byte)recordLength);
        WriteBothEndianInt32(image, offset + 2, sector);
        WriteBothEndianInt32(image, offset + 10, length);
        image[offset + 18] = 126; image[offset + 19] = 1; image[offset + 20] = 1;
        image[offset + 25] = isDirectory ? (byte)2 : (byte)0;
        WriteBothEndianInt16(image, offset + 28, 1);
        image[offset + 32] = checked((byte)identifier.Length);
        Buffer.BlockCopy(identifier, 0, image, offset + 33, identifier.Length);
        return recordLength;
    }

    private static List<IsoEntry> ReadDirectory(byte[] image, int sector)
    {
        RequireSector(image, sector);
        var result = new List<IsoEntry>();
        var offset = sector * SectorSize;
        var end = offset + SectorSize;
        while (offset < end && image[offset] != 0)
        {
            var recordLength = image[offset];
            if (recordLength < 34 || offset + recordLength > end)
                throw new InvalidDataException("Invalid ISO directory record.");
            var identifierLength = image[offset + 32];
            var name = Encoding.ASCII.GetString(image, offset + 33, identifierLength);
            if (identifierLength > 1)
                result.Add(new(name, ReadInt32LittleEndian(image, offset + 2), ReadInt32LittleEndian(image, offset + 10)));
            offset += recordLength;
        }
        return result;
    }

    private static string ReplaceToken(string template, string token, string script)
    {
        var idx = template.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0) return template;
        var lineStart = template.LastIndexOf('\n', idx) + 1;
        var indent = template[lineStart..idx];
        var normalized = script.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        return template.Replace(token, string.Join("\n" + indent, normalized.Split('\n')), StringComparison.Ordinal);
    }

    private static int SectorsFor(int length) => Math.Max(1, (length + SectorSize - 1) / SectorSize);
    private static void RequireSector(byte[] image, int sector)
    {
        if (image.Length < (sector + 1) * SectorSize) throw new InvalidDataException("ISO is truncated.");
    }
    private static void WriteAscii(byte[] b, int o, string value, int length = 0)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (length == 0) length = bytes.Length;
        Array.Fill(b, (byte)' ', o, length);
        Buffer.BlockCopy(bytes, 0, b, o, Math.Min(bytes.Length, length));
    }
    private static void WriteBothEndianInt16(byte[] b, int o, int v) { WriteInt16LittleEndian(b, o, v); WriteInt16BigEndian(b, o + 2, v); }
    private static void WriteBothEndianInt32(byte[] b, int o, int v) { WriteInt32LittleEndian(b, o, v); WriteInt32BigEndian(b, o + 4, v); }
    private static void WriteInt16LittleEndian(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    private static void WriteInt16BigEndian(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    private static void WriteInt32LittleEndian(byte[] b, int o, int v) { for (var i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
    private static void WriteInt32BigEndian(byte[] b, int o, int v) { for (var i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * (3 - i))); }
    private static int ReadInt32LittleEndian(byte[] b, int o) => b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24;

    private readonly record struct IsoEntry(string Name, int Sector, int Length);
}

public sealed record SeedVerificationResult(bool LabelOk, bool HasUserData, bool HasMetaData)
{
    public string? Error { get; init; }
    public bool IsValid => LabelOk && HasUserData && HasMetaData && Error is null;
}
