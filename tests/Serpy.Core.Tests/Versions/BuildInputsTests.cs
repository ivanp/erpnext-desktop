using System.Text.Json;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Versions;

public sealed class BuildInputsTests
{
    /// <summary>
    /// When true, allows parts in the shipped build-inputs.json to have empty file lists.
    /// The generator unit (U14 generator half) flips this to false when real closures are committed.
    /// </summary>
    private static readonly bool AllowPlaceholderParts = true;

    [Fact]
    public void ShippedBuildInputs_LoadsAndMeetsBasicInvariants()
    {
        var inputs = BuildInputsLoader.Load();

        Assert.NotNull(inputs);
        Assert.True(inputs.SchemaVersion > 0);
        Assert.NotNull(inputs.Versions);
        Assert.NotNull(inputs.Parts);

        Assert.Equal(8, inputs.Parts.Count);
        Assert.Equal(BuildInputs.FixedPartIds, inputs.Parts.Select(p => p.Id).ToList());

        var keysSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in inputs.Parts)
        {
            Assert.False(string.IsNullOrWhiteSpace(part.Id));
            Assert.False(string.IsNullOrWhiteSpace(part.Title));
            Assert.NotNull(part.Files);

            if (!AllowPlaceholderParts)
            {
                Assert.NotEmpty(part.Files);
            }

            foreach (var file in part.Files)
            {
                Assert.False(string.IsNullOrWhiteSpace(file.Key));
                Assert.True(keysSeen.Add(file.Key), $"Duplicate cache key found: '{file.Key}'");

                Assert.NotNull(file.Path);
                Assert.True(file.Size > 0, $"File '{file.Key}' size must be > 0, was {file.Size}");
                Assert.NotNull(file.Urls);
                Assert.NotEmpty(file.Urls);

                // Exactly one checksum
                var hasSha256 = !string.IsNullOrWhiteSpace(file.Sha256);
                var hasSha512 = !string.IsNullOrWhiteSpace(file.Sha512);
                Assert.True(hasSha256 ^ hasSha512, $"File '{file.Key}' must have exactly one of sha256 or sha512");

                if (hasSha256)
                {
                    Assert.Equal(64, file.Sha256!.Length);
                    Assert.All(file.Sha256, c => Assert.True(Uri.IsHexDigit(c)));
                }

                if (hasSha512)
                {
                    Assert.Equal(128, file.Sha512!.Length);
                    Assert.All(file.Sha512, c => Assert.True(Uri.IsHexDigit(c)));
                }

                foreach (var url in file.Urls)
                {
                    Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
                    Assert.Equal("https", uri.Scheme);
                    Assert.Contains(uri.Host, BuildInputs.AllowedHosts);
                }
            }
        }
    }

    [Fact]
    public void Covers_AE10_VersionsAndCommitsInList_EqualLocksInVersionsYaml()
    {
        var manifest = VersionManifestLoader.Load();
        var inputs = BuildInputsLoader.Load();

        Assert.NotNull(inputs.Versions);
        Assert.Equal(manifest.Runtime.Python.Lock, inputs.Versions.PythonLock);
        Assert.Equal(manifest.Runtime.Node.Lock, inputs.Versions.NodeLock);
        Assert.Equal(manifest.Runtime.MariaDb.Lock, inputs.Versions.MariadbLock);
        Assert.Equal(manifest.Runtime.Redis.Lock, inputs.Versions.RedisLock);

        Assert.Equal(manifest.Apps.Frappe.Commit, inputs.Versions.FrappeCommit);
        Assert.Equal(manifest.Apps.ErpNext.Commit, inputs.Versions.ErpnextCommit);
        Assert.Equal(manifest.Apps.Frappe.Lock, inputs.Versions.FrappeLock);
        Assert.Equal(manifest.Apps.ErpNext.Lock, inputs.Versions.ErpnextLock);
    }

    [Fact]
    public void UnknownField_IsIgnoredDuringLoad()
    {
        var json = """
        {
          "extraUnknownField": "should be ignored",
          "schemaVersion": 1,
          "versions": {
            "pythonLock": "3.14.7",
            "nodeLock": "24.2.0",
            "mariadbLock": "11.8.3",
            "redisLock": "8.0.2",
            "frappeCommit": "6a329d068416768ec47ccd3326b9cc95a8d7bf99",
            "erpnextCommit": "11e0ba0a1c45f217e2e73e885f699102d06da325",
            "frappeLock": "16.31.0",
            "erpnextLock": "16.32.3",
            "customFutureRuntimeField": 12345
          },
          "parts": [
            {
              "id": "engine",
              "title": "Serpy engine",
              "files": []
            }
          ]
        }
        """;

        var inputs = BuildInputsLoader.Parse(json);
        Assert.NotNull(inputs);
        Assert.Equal(1, inputs.SchemaVersion);
        Assert.Single(inputs.Parts!);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("versions")]
    [InlineData("pythonLock")]
    [InlineData("nodeLock")]
    [InlineData("mariadbLock")]
    [InlineData("redisLock")]
    [InlineData("frappeCommit")]
    [InlineData("erpnextCommit")]
    [InlineData("frappeLock")]
    [InlineData("erpnextLock")]
    [InlineData("parts")]
    public void MissingRequiredField_ThrowsInvalidDataExceptionNamingField(string fieldToOmit)
    {
        var jsonObject = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["versions"] = new Dictionary<string, object?>
            {
                ["pythonLock"] = "3.14.7",
                ["nodeLock"] = "24.2.0",
                ["mariadbLock"] = "11.8.3",
                ["redisLock"] = "8.0.2",
                ["frappeCommit"] = "6a329d068416768ec47ccd3326b9cc95a8d7bf99",
                ["erpnextCommit"] = "11e0ba0a1c45f217e2e73e885f699102d06da325",
                ["frappeLock"] = "16.31.0",
                ["erpnextLock"] = "16.32.3"
            },
            ["parts"] = new List<object>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "engine",
                    ["title"] = "Serpy engine",
                    ["files"] = new List<object>()
                }
            }
        };

        if (fieldToOmit == "schemaVersion") jsonObject.Remove("schemaVersion");
        else if (fieldToOmit == "versions") jsonObject.Remove("versions");
        else if (fieldToOmit == "parts") jsonObject.Remove("parts");
        else
        {
            var versions = (Dictionary<string, object?>)jsonObject["versions"]!;
            versions.Remove(fieldToOmit);
        }

        var json = JsonSerializer.Serialize(jsonObject);
        var ex = Assert.Throws<InvalidDataException>(() => BuildInputsLoader.Parse(json));
        Assert.Contains(fieldToOmit, ex.Message);
    }

    [Fact]
    public void MissingFileField_ThrowsInvalidDataExceptionNamingField()
    {
        var json = """
        {
          "schemaVersion": 1,
          "versions": {
            "pythonLock": "3.14.7",
            "nodeLock": "24.2.0",
            "mariadbLock": "11.8.3",
            "redisLock": "8.0.2",
            "frappeCommit": "6a329d068416768ec47ccd3326b9cc95a8d7bf99",
            "erpnextCommit": "11e0ba0a1c45f217e2e73e885f699102d06da325",
            "frappeLock": "16.31.0",
            "erpnextLock": "16.32.3"
          },
          "parts": [
            {
              "id": "engine",
              "title": "Serpy engine",
              "files": [
                {
                  "path": "",
                  "size": 100,
                  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "urls": ["https://qemu.weilnetz.de/setup.exe"],
                  "inGuest": false
                }
              ]
            }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidDataException>(() => BuildInputsLoader.Parse(json));
        Assert.Contains("key", ex.Message);
    }

    [Fact]
    public void FileMissingChecksum_ThrowsNamingSha()
    {
        var json = """
        {
          "schemaVersion": 1,
          "versions": {
            "pythonLock": "3.14.7",
            "nodeLock": "24.2.0",
            "mariadbLock": "11.8.3",
            "redisLock": "8.0.2",
            "frappeCommit": "6a329d068416768ec47ccd3326b9cc95a8d7bf99",
            "erpnextCommit": "11e0ba0a1c45f217e2e73e885f699102d06da325",
            "frappeLock": "16.31.0",
            "erpnextLock": "16.32.3"
          },
          "parts": [
            {
              "id": "engine",
              "title": "Serpy engine",
              "files": [
                {
                  "key": "test.exe",
                  "path": "",
                  "size": 100,
                  "urls": ["https://qemu.weilnetz.de/setup.exe"],
                  "inGuest": false
                }
              ]
            }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidDataException>(() => BuildInputsLoader.Parse(json));
        Assert.Contains("sha", ex.Message);
    }
}
