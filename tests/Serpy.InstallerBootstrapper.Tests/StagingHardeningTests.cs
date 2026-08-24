using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Serpy.InstallerBootstrapper.Tests;

/// <summary>
/// Unit tests for <see cref="StagingHardening"/> -- the reparse-point rejection
/// and ownership/ACL-hardening logic the elevated bootstrapper applies to its
/// admin-only intermediate and the final staging tree (IU1 step 4).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StagingHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"SerpyStagingTest-{Guid.NewGuid():N}");

    public StagingHardeningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // .NET's recursive Directory.Delete tries to descend into a
        // junction's target and fails ("The parameter is incorrect") --
        // remove any reparse points as their own (non-recursive) delete
        // first, which correctly detaches just the junction stub.
        foreach (var entry in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(entry);
        }
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ContainsReparsePoint_PlainTree_ReturnsFalse()
    {
        var subdir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subdir);
        File.WriteAllText(Path.Combine(subdir, "file.txt"), "hello");

        Assert.False(StagingHardening.ContainsReparsePoint(_root, out var reparsePointPath));
        Assert.Null(reparsePointPath);
    }

    [Fact]
    public void ContainsReparsePoint_JunctionPresent_ReturnsTrueWithPath()
    {
        // Directory junctions (unlike symlinks) never require elevation or
        // Developer Mode to create -- exactly the kind of reparse point a
        // non-admin requesting user could plant inside their own runtime
        // tree to redirect the elevated installer's writes (IR3).
        var targetDir = Path.Combine(_root, "junction-target");
        Directory.CreateDirectory(targetDir);
        var junctionPath = Path.Combine(_root, "junction-link");
        CreateJunction(junctionPath, targetDir);

        var found = StagingHardening.ContainsReparsePoint(_root, out var reparsePointPath);

        Assert.True(found);
        Assert.Equal(junctionPath, reparsePointPath);
    }

    [Fact]
    public void ContainsReparsePoint_JunctionNestedDeep_StillDetected()
    {
        var nested = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(nested);
        var targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetDir);
        var junctionPath = Path.Combine(nested, "junction-link");
        CreateJunction(junctionPath, targetDir);

        Assert.True(StagingHardening.ContainsReparsePoint(_root, out var reparsePointPath));
        Assert.Equal(junctionPath, reparsePointPath);
    }

    [Fact]
    public void CreateLockedDirectory_GrantsOnlyAdministratorsAndSystem()
    {
        var path = Path.Combine(_root, "locked");

        StagingHardening.CreateLockedDirectory(path);

        Assert.True(Directory.Exists(path));
        var acl = new DirectoryInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); // no inherited rules leaking in from the parent

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.True(sid.Equals(adminsSid) || sid.Equals(systemSid),
                $"Unexpected principal granted access to a locked staging directory: {sid}");
        }
    }

    [Fact]
    public void CreateLockedDirectory_RejectsPreExistingPath()
    {
        // An attacker (the unprivileged requesting user, who controls their
        // own runtime tree where staging directories are created) could
        // pre-create the exact path an elevated process is about to trust
        // as fresh -- CreateLockedDirectory must fail closed rather than
        // silently locking down and reusing whatever is already there.
        var path = Path.Combine(_root, "already-there");
        Directory.CreateDirectory(path);

        Assert.Throws<IOException>(() => StagingHardening.CreateLockedDirectory(path));
    }

    [Fact]
    public void CreateLockedDirectory_ConcurrentCallers_ExactlyOneWinsAndLeafIsProtected()
    {
        // Proves the fix is genuinely atomic, not merely "usually wins a
        // race in practice": many callers race for the identical path with
        // a shared start gate; exactly one may succeed, every other must
        // observe IOException, and the surviving leaf must carry only the
        // winner's admin/SYSTEM-only DACL -- never a transiently
        // unprotected or loser-influenced state.
        var path = Path.Combine(_root, "race-target");
        const int callers = 16;
        using var startGate = new ManualResetEventSlim(initialState: false);
        var results = new Exception?[callers];
        var threads = new Thread[callers];

        for (var i = 0; i < callers; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                startGate.Wait();
                try
                {
                    StagingHardening.CreateLockedDirectory(path);
                    results[index] = null;
                }
                catch (Exception ex)
                {
                    results[index] = ex;
                }
            });
            threads[i].Start();
        }
        startGate.Set();
        foreach (var t in threads) t.Join();

        var winners = results.Count(r => r is null);
        var losers = results.Where(r => r is not null).ToArray();
        Assert.Equal(1, winners);
        Assert.Equal(callers - 1, losers.Length);
        Assert.All(losers, r => Assert.IsType<IOException>(r));

        Assert.True(Directory.Exists(path));
        var acl = new DirectoryInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.True(sid.Equals(adminsSid) || sid.Equals(systemSid),
                $"A losing caller left an unexpected grant for '{sid}' on the raced leaf.");
        }
    }

    [Fact]
    public void HardenOwnership_AppliesOwnerRecursivelyToPreExistingDescendants()
    {
        // New inheritable ACEs added to a directory do NOT retroactively
        // apply to files already created inside it -- only to items created
        // afterward. This is exactly why HardenOwnership must walk every
        // existing descendant explicitly rather than relying on inheritance
        // alone; this test proves the recursive walk actually reaches a
        // nested file, not just the root.
        var stagingRoot = Path.Combine(_root, "staging");
        var nestedDir = Path.Combine(stagingRoot, "nested");
        Directory.CreateDirectory(nestedDir);
        var nestedFile = Path.Combine(nestedDir, "payload.txt");
        File.WriteAllText(nestedFile, "installed content");

        var ownerSid = WindowsIdentity.GetCurrent().User!;
        StagingHardening.HardenOwnership(stagingRoot, ownerSid);

        var rootOwner = (SecurityIdentifier)new DirectoryInfo(stagingRoot).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        var nestedDirOwner = (SecurityIdentifier)new DirectoryInfo(nestedDir).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;
        var nestedFileOwner = (SecurityIdentifier)new FileInfo(nestedFile).GetAccessControl().GetOwner(typeof(SecurityIdentifier))!;

        Assert.Equal(ownerSid, rootOwner);
        Assert.Equal(ownerSid, nestedDirOwner);
        Assert.Equal(ownerSid, nestedFileOwner);

        // The owner SID also has an explicit, non-inherited full-control
        // grant on the nested file -- not merely "owner" metadata with no
        // actual access -- so the requesting user can genuinely
        // read/update/delete their own installed runtime afterward.
        var fileAcl = new FileInfo(nestedFile).GetAccessControl();
        var fileRules = fileAcl.GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier));
        var ownerHasFullControl = fileRules.Cast<FileSystemAccessRule>().Any(r =>
            r.AccessControlType == AccessControlType.Allow &&
            ((SecurityIdentifier)r.IdentityReference).Equals(ownerSid) &&
            r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.True(ownerHasFullControl);
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(junctionPath);
        psi.ArgumentList.Add(targetPath);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J failed: {proc.StandardError.ReadToEnd()}");
    }
}
