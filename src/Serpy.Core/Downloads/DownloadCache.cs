using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Serpy.Core.Configuration;
using Serpy.Core.Versions;

namespace Serpy.Core.Downloads;

public sealed class DownloadCache
{
    private readonly ConcurrentDictionary<string, bool> _verifiedInRun = new(StringComparer.OrdinalIgnoreCase);

    public string CacheDirectory { get; }

    public DownloadCache(string? cacheDirectory = null)
    {
        CacheDirectory = cacheDirectory ?? KnownPaths.CacheDir;
    }

    public string GetPath(string key)
    {
        return Path.Combine(CacheDirectory, GetFileName(key));
    }

    public string GetPartPath(string key)
    {
        return GetPath(key) + ".part";
    }

    public bool Contains(string key)
    {
        return File.Exists(GetPath(key));
    }

    public bool IsVerified(string key)
    {
        return _verifiedInRun.ContainsKey(key);
    }

    public void RecordVerified(string key)
    {
        _verifiedInRun.TryAdd(key, true);
    }

    public void Invalidate(string key)
    {
        _verifiedInRun.TryRemove(key, out _);
        var path = GetPath(key);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
        var partPath = GetPartPath(key);
        if (File.Exists(partPath))
        {
            try { File.Delete(partPath); } catch { }
        }
    }

    public bool TryVerify(InputFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Key)) return false;

        if (_verifiedInRun.ContainsKey(file.Key))
            return true;

        var path = GetPath(file.Key);
        if (!File.Exists(path)) return false;

        try
        {
            var info = new FileInfo(path);
            if (file.Size > 0 && info.Length != file.Size)
            {
                File.Delete(path);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(file.Sha256))
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(path);
                var actualHex = Convert.ToHexString(sha.ComputeHash(stream));
                if (!string.Equals(actualHex, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(file.Sha512))
            {
                using var sha = SHA512.Create();
                using var stream = File.OpenRead(path);
                var actualHex = Convert.ToHexString(sha.ComputeHash(stream));
                if (!string.Equals(actualHex, file.Sha512, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                    return false;
                }
            }

            RecordVerified(file.Key);
            return true;
        }
        catch
        {
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
            return false;
        }
    }

    public void DeleteLeftovers()
    {
        if (!Directory.Exists(CacheDirectory)) return;

        try
        {
            foreach (var partFile in Directory.EnumerateFiles(CacheDirectory, "*.part", SearchOption.AllDirectories))
            {
                try { File.Delete(partFile); } catch { }
            }
        }
        catch
        {
            // Best effort leftover cleanup
        }
    }

    public static string GetFileName(string key)
    {
        if (key.Length <= 100 && IsSafeFileName(key))
            return key;

        var ext = Path.GetExtension(key);
        if (ext.Length > 15) ext = "";
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key)))[..24];
        var prefix = Sanitize(Path.GetFileNameWithoutExtension(key));
        if (prefix.Length > 60) prefix = prefix[..60];
        return $"{prefix}_{hash}{ext}";
    }

    private static bool IsSafeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in s)
        {
            if (invalid.Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                return false;
        }
        return true;
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(invalid.Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }
        return sb.ToString();
    }
}
