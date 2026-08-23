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
    /// Constant-time nonce comparison — a nonce feeds an authentication decision,
    /// so a timing side-channel on early-exit comparison is avoidable at negligible cost.
    /// </summary>
    public static bool NoncesMatch(string expected, string actual)
    {
        if (expected.Length != actual.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected),
            System.Text.Encoding.ASCII.GetBytes(actual));
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
        var current = fullDestination;
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
