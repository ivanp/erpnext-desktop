using System.Security.Cryptography;

namespace Serpy.Core.Qemu;

/// <summary>
/// Shared, elevation-free protocol pieces for the authenticated handoff between
/// the non-elevated app and the elevated <c>Serpy.InstallerBootstrapper.exe</c>
/// helper (IR3/IKTD1/IU1 step 7).
///
/// The helper hosts the named-pipe <b>server</b> (so it can impersonate the
/// connecting client and read the client's real token SID); the app connects
/// as <b>client</b>. Bootstrap arguments (pipe name, nonce, claimed SID) are
/// never trusted on their own — they are only ever validated against the
/// pipe-authenticated connection. This class holds the pure, host-independent
/// pieces of that contract: nonce generation/comparison and path validation.
/// Actual pipe/ACL/impersonation Win32 calls live in the separate
/// <c>Serpy.InstallerBootstrapper</c> executable project, not here.
/// </summary>
public static class BootstrapProtocol
{
    private const int NonceByteLength = 32;

    /// <summary>Generate a fresh high-entropy single-use nonce for one bootstrap handoff.</summary>
    public static string CreateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceByteLength));

    /// <summary>
    /// Constant-time nonce comparison. A nonce is always hex-encoded output from
    /// <see cref="CreateNonce"/>: exactly 64 ASCII hex characters (32 decoded bytes).
    /// A candidate that isn't 64 hex chars is rejected outright — never compared as
    /// raw text, because <see cref="System.Text.Encoding.ASCII"/> maps every non-ASCII
    /// character to the same '?' byte, so two distinct non-hex strings of equal length
    /// could otherwise collide under a naive byte comparison.
    /// </summary>
    public static bool NoncesMatch(string expected, string actual)
    {
        if (!TryDecodeNonce(expected, out var expectedBytes)) return false;
        if (!TryDecodeNonce(actual, out var actualBytes)) return false;
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static bool TryDecodeNonce(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length != NonceByteLength * 2) return false;
        foreach (var c in value)
            if (!Uri.IsHexDigit(c)) return false;
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == NonceByteLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validate a candidate <b>source</b> installer path before the elevated helper
    /// touches it: it must be an existing regular file, not a reparse point
    /// (symlink/junction) and not a device/UNC path masquerading as local.
    /// Throws <see cref="InvalidOperationException"/> naming the violation; never
    /// silently substitutes a "safe" path.
    /// </summary>
    public static void ValidateSourceFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Bootstrap request source path is empty.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"Bootstrap request source is a UNC path, rejected: {path}");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Bootstrap request source does not exist: {path}");

        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException(
                $"Bootstrap request source is a reparse point (symlink/junction), rejected: {path}");
    }

    /// <summary>
    /// Validate that a candidate <b>destination</b> directory canonicalizes strictly
    /// under <paramref name="allowedRoot"/> — no <c>..</c> traversal, no reparse point
    /// anywhere on the resolved path, no UNC/<c>\\?\</c> escape. The allowed root is
    /// always the authenticated original-user's own runtime directory (never a
    /// caller-supplied SID/path); this function only checks containment.
    /// </summary>
    public static void ValidateDestinationUnderRoot(string destination, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("Bootstrap request destination is empty.");
        if (destination.StartsWith(@"\\", StringComparison.Ordinal) ||
            destination.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new InvalidOperationException($"Bootstrap request destination is a UNC/device path, rejected: {destination}");

        var fullDestination = Path.GetFullPath(destination);
        var fullRoot = Path.GetFullPath(allowedRoot);
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullDestination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullDestination, fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Bootstrap request destination '{destination}' escapes the allowed root '{allowedRoot}'.");

        // Walk every existing ancestor segment under the root and reject a reparse point
        // anywhere in the chain — a symlinked intermediate directory could otherwise
        // redirect a contained-looking path outside the allowed root at resolve time.
        // A fresh (not-yet-created) destination has no existing entry at the leaf, so the
        // walk must start from the nearest existing ancestor, not the destination itself.
        var current = fullDestination;
        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var next = Path.GetDirectoryName(current);
            if (next is null || string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
                return; // reached a drive root with nothing existing — nothing to check
            current = next;
        }
        while (Directory.Exists(current) || File.Exists(current))
        {
            var attrs = File.GetAttributes(current);
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException(
                    $"Bootstrap request destination path contains a reparse point at '{current}', rejected.");

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
}
