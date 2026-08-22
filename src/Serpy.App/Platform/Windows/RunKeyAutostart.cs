using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Serpy.App.Platform.Windows;

/// <summary>
/// Manages the Windows per-user sign-in startup Run-key entry (KTD13, AE7).
///
/// Registration guards (all must pass before writing HKCU):
///   1. Target executable path must not be a reparse point.
///   2. Executable's directory DACL must not permit writes by non-owner, non-admin principals.
///   3. Fully-quoted command must not exceed 260 characters (documented Run-key limit).
///   4. Path must be canonicalised and stable.
///
/// Unregistration removes only Serpy's named value; all other Run values are untouched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunKeyAutostart
{
    private const string RunKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Serpy";
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

    /// <summary>Returns true if the Run-key entry currently exists for this user.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    /// <summary>
    /// Register Serpy.App in the current-user Run key.
    /// Throws <see cref="AutostartException"/> if any guard fails.
    /// </summary>
    public static void Enable(string executablePath)
    {
        var canonical = Canonicalise(executablePath);
        GuardReparsePoint(canonical);
        GuardDacl(Path.GetDirectoryName(canonical)!);

        var command = $"\"{canonical}\" --tray";
        if (command.Length > MaxCommandLength)
            throw new AutostartException(
                $"Autostart command exceeds {MaxCommandLength} characters ({command.Length}). " +
                "Move the application to a shorter path.");

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new AutostartException($"Cannot open registry key: HKCU\\{RunKeyPath}");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    /// <summary>Remove only Serpy's Run-key entry. All other values are untouched.</summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
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

public sealed class AutostartException(string message) : Exception(message);
