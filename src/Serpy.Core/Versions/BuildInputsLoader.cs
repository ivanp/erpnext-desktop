using System.Text.Json;

namespace Serpy.Core.Versions;

public static class BuildInputsLoader
{
    public static BuildInputs Load()
    {
        var path = ResolveJsonPath()
            ?? throw new FileNotFoundException(
                "config/build-inputs.json not found alongside the executable or in a parent directory.");
        return LoadFrom(path);
    }

    public static BuildInputs LoadFrom(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public static BuildInputs Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        ValidateDocument(doc.RootElement);

        var inputs = JsonSerializer.Deserialize(json, BuildInputsJsonContext.Default.BuildInputs)
            ?? throw new InvalidDataException("JSON payload produced null BuildInputs.");
        Validate(inputs);
        return inputs;
    }

    private static void ValidateDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("BuildInputs root must be a JSON object.");

        if (!root.TryGetProperty("schemaVersion", out var schemaVersion) || schemaVersion.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException("Missing required field 'schemaVersion'");

        if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing required field 'versions'");

        string[] requiredVersions =
        [
            "pythonLock",
            "nodeLock",
            "mariadbLock",
            "redisLock",
            "frappeCommit",
            "erpnextCommit",
            "frappeLock",
            "erpnextLock"
        ];

        foreach (var field in requiredVersions)
        {
            if (!versions.TryGetProperty(field, out var val) || val.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Missing required field '{field}'");
        }

        if (!root.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing required field 'parts'");

        foreach (var part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("id", out var idVal) || idVal.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Missing required field 'id'");
            if (!part.TryGetProperty("title", out var titleVal) || titleVal.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Missing required field 'title'");
            if (!part.TryGetProperty("files", out var filesVal) || filesVal.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Missing required field 'files'");

            foreach (var file in filesVal.EnumerateArray())
            {
                if (!file.TryGetProperty("key", out var keyVal) || keyVal.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Missing required field 'key'");
                if (!file.TryGetProperty("path", out var pathVal) || pathVal.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Missing required field 'path'");
                if (!file.TryGetProperty("size", out var sizeVal) || sizeVal.ValueKind != JsonValueKind.Number)
                    throw new InvalidDataException("Missing required field 'size'");
                if (!file.TryGetProperty("inGuest", out var inGuestVal) || inGuestVal.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Missing required field 'inGuest'");
                if (!file.TryGetProperty("urls", out var urlsVal) || urlsVal.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Missing required field 'urls'");

                var hasSha256 = file.TryGetProperty("sha256", out var sha256Val) && sha256Val.ValueKind == JsonValueKind.String;
                var hasSha512 = file.TryGetProperty("sha512", out var sha512Val) && sha512Val.ValueKind == JsonValueKind.String;

                if (!hasSha256 && !hasSha512)
                    throw new InvalidDataException("Missing required field 'sha256' or 'sha512'");
                if (hasSha256 && hasSha512)
                    throw new InvalidDataException("File cannot have both 'sha256' and 'sha512'");
            }
        }
    }

    public static void Validate(BuildInputs inputs)
    {
        if (inputs.SchemaVersion <= 0)
            throw new InvalidDataException("Missing required field 'schemaVersion'");

        var v = inputs.Versions;
        if (string.IsNullOrWhiteSpace(v.PythonLock))
            throw new InvalidDataException("Missing required field 'pythonLock'");
        if (string.IsNullOrWhiteSpace(v.NodeLock))
            throw new InvalidDataException("Missing required field 'nodeLock'");
        if (string.IsNullOrWhiteSpace(v.MariadbLock))
            throw new InvalidDataException("Missing required field 'mariadbLock'");
        if (string.IsNullOrWhiteSpace(v.RedisLock))
            throw new InvalidDataException("Missing required field 'redisLock'");
        if (string.IsNullOrWhiteSpace(v.FrappeCommit))
            throw new InvalidDataException("Missing required field 'frappeCommit'");
        if (string.IsNullOrWhiteSpace(v.ErpnextCommit))
            throw new InvalidDataException("Missing required field 'erpnextCommit'");
        if (string.IsNullOrWhiteSpace(v.FrappeLock))
            throw new InvalidDataException("Missing required field 'frappeLock'");
        if (string.IsNullOrWhiteSpace(v.ErpnextLock))
            throw new InvalidDataException("Missing required field 'erpnextLock'");

        var partIdsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in inputs.Parts)
        {
            if (string.IsNullOrWhiteSpace(part.Id))
                throw new InvalidDataException("Missing required field 'id'");
            if (string.IsNullOrWhiteSpace(part.Title))
                throw new InvalidDataException("Missing required field 'title'");

            partIdsSeen.Add(part.Id);

            foreach (var file in part.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Key))
                    throw new InvalidDataException("Missing required field 'key'");
                if (file.Size <= 0)
                    throw new InvalidDataException("Missing required field 'size'");
                if (file.Urls.Count == 0)
                    throw new InvalidDataException("Missing required field 'urls'");

                var hasSha256 = !string.IsNullOrWhiteSpace(file.Sha256);
                var hasSha512 = !string.IsNullOrWhiteSpace(file.Sha512);

                if (!hasSha256 && !hasSha512)
                    throw new InvalidDataException("Missing required field 'sha256' or 'sha512'");
                if (hasSha256 && hasSha512)
                    throw new InvalidDataException("File cannot have both 'sha256' and 'sha512'");

                if (hasSha256)
                {
                    if (file.Sha256!.Length != 64 || !IsHexString(file.Sha256))
                        throw new InvalidDataException($"Invalid sha256 checksum format for file '{file.Key}'");
                }

                if (hasSha512)
                {
                    if (file.Sha512!.Length != 128 || !IsHexString(file.Sha512))
                        throw new InvalidDataException($"Invalid sha512 checksum format for file '{file.Key}'");
                }

                foreach (var url in file.Urls)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidDataException($"File '{file.Key}' URL '{url}' is not a valid HTTPS address.");

                    if (!BuildInputs.AllowedHosts.Contains(uri.Host))
                        throw new InvalidDataException($"File '{file.Key}' host '{uri.Host}' is not in the allowed publishers list.");
                }
            }
        }
    }

    private static bool IsHexString(string s)
    {
        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private static string? ResolveJsonPath()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "config", "build-inputs.json");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return null;
    }
}
