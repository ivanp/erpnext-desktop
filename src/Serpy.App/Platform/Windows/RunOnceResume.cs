using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Serpy.Core.Configuration;

namespace Serpy.App.Platform.Windows;

/// <summary>
/// Manages the Windows per-user RunOnce entry for resuming setup after reboot (KTD15, U7).
///
/// Follows <see cref="RunKeyAutostart"/> with the same security guards:
///   1. Target executable path must not be a reparse point.
///   2. Executable's directory DACL must not permit writes by non-owner, non-admin principals.
///   3. Fully-quoted command must not exceed 260 characters (documented Run/RunOnce limit).
///   4. Path must be canonicalised and stable.
///
/// Scoped to the application data root so that distinct instances/test environments
/// do not collide on registry value names.
/// Written only on explicit "Restart now", and removed when Serpy starts with --resume-setup.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunOnceResume
{
    public const string DefaultRunOnceKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const int MaxCommandLength = 260;

    private const FileSystemRights WriteCapableRights =
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes;

    /// <summary>
    /// Resolves the RunOnce registry value name scoped to the given (or default) application data root.
    /// </summary>
    public static string ResolveValueName(string? appDataRoot = null)
    {
        var root = appDataRoot ?? KnownPaths.AppDataRoot;
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(root)));
        return $"SerpyResume_{hash[..8]}";
    }

    /// <summary>Returns true if the RunOnce entry currently exists for this user and app data root.</summary>
    public static bool IsEnabled(string? appDataRoot = null, string? subKeyPath = null)
    {
        var keyPath = subKeyPath ?? DefaultRunOnceKeyPath;
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ResolveValueName(appDataRoot)) is not null;
    }

    /// <summary>
    /// Register Serpy to resume setup in the current-user RunOnce key.
    /// Throws <see cref="AutostartException"/> if any security guard fails.
    /// </summary>
    public static void Enable(string executablePath, string? appDataRoot = null, string? subKeyPath = null)
    {
        var canonical = Canonicalise(executablePath);
        GuardReparsePoint(canonical);
        GuardDacl(Path.GetDirectoryName(canonical)!);

        var command = $"\"{canonical}\" --resume-setup";
        if (command.Length > MaxCommandLength)
            throw new AutostartException(
                $"RunOnce command exceeds {MaxCommandLength} characters ({command.Length}). " +
                "Move the application to a shorter path.");

        var keyPath = subKeyPath ?? DefaultRunOnceKeyPath;
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new AutostartException($"Cannot open registry key: HKCU\\{keyPath}");
        key.SetValue(ResolveValueName(appDataRoot), command, RegistryValueKind.String);
    }

    /// <summary>Remove only Serpy's RunOnce entry for this app data root. All other values are untouched.</summary>
    public static void Disable(string? appDataRoot = null, string? subKeyPath = null)
    {
        var keyPath = subKeyPath ?? DefaultRunOnceKeyPath;
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(ResolveValueName(appDataRoot), throwOnMissingValue: false);
    }

    /// <summary>Returns the registered command string if present, or null.</summary>
    public static string? GetRegisteredCommand(string? appDataRoot = null, string? subKeyPath = null)
    {
        var keyPath = subKeyPath ?? DefaultRunOnceKeyPath;
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(ResolveValueName(appDataRoot)) as string;
    }

    // ── Guards ────────────────────────────────────────────────────────────────

    private static string Canonicalise(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new AutostartException($"Executable not found: {full}");
        return full;
    }

    private static void GuardReparsePoint(string path)
    {
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new AutostartException(
                $"Executable is a reparse point (junction/symlink): {path}. " +
                "Run Serpy from a canonical path.");

        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new AutostartException(
                $"Executable directory is a reparse point: {dir.FullName}. " +
                "Run Serpy from a canonical path.");
    }

    private static void GuardDacl(string directory)
    {
        var di = new DirectoryInfo(directory);
        var acl = di.GetAccessControl();
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true,
            targetType: typeof(SecurityIdentifier));

        var ownerSid    = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var adminsSid   = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid   = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var currentUser = WindowsIdentity.GetCurrent().User;

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!IsWriteCapable(rule.FileSystemRights)) continue;

            var sid = rule.IdentityReference as SecurityIdentifier;
            if (sid is null) continue;

            // Allow owner, admins, and SYSTEM.
            if ((currentUser is not null && sid.Equals(currentUser)) ||
                (ownerSid    is not null && sid.Equals(ownerSid))    ||
                sid.Equals(adminsSid) ||
                sid.Equals(systemSid))
                continue;

            // Everyone/Users/other writeable → reject.
            throw new AutostartException(
                $"Executable directory '{directory}' is writable by '{sid}'. " +
                "Install Serpy in a location writable only by the owner and administrators " +
                "before enabling sign-in startup.");
        }
    }

    /// <summary>Whether an ACL rights mask can alter a directory or its executable contents.</summary>
    public static bool IsWriteCapable(FileSystemRights rights) =>
        (rights & WriteCapableRights) != 0;
}
