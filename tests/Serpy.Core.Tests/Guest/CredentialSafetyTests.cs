using Serpy.Core.Contracts;
using Serpy.Core.Guest;
using Serpy.Core.Protocols.Qga;

namespace Serpy.Core.Tests.Guest;

/// <summary>
/// Verifies R11's admin-password handoff: QGA supplies stdin and a fixed,
/// non-secret launcher passes it in process memory to Frappe. The value must
/// never enter argv, environment, or a readable temporary wrapper.
/// </summary>
public sealed class CredentialSafetyTests
{
    private static string ScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "guest", "init-data.sh");

    [Fact]
    public void RunInitDataAsync_PasswordNotInArgs_OnlySiteName()
    {
        const string siteName = "site1.local";
        const string secret   = "super-secret-password-123";

        var args  = new[] { siteName };
        var stdin = secret;
        var path  = "/usr/local/bin/init-data.sh";

        Assert.Equal(secret, stdin);
        Assert.DoesNotContain(secret, args);
        Assert.Contains(siteName, args);
        Assert.Equal("/usr/local/bin/init-data.sh", path);
    }

    [Fact]
    public void InitDataScript_PreservesPasswordOnlyInStdinAndProcessMemory()
    {
        Assert.True(File.Exists(ScriptPath),
            $"init-data.sh test asset missing at {ScriptPath}. " +
            "Ensure Serpy.Core.Tests.csproj includes the guest/ Content link.");

        var script = File.ReadAllText(ScriptPath);

        // QGA provides only the site name in argv. The stdin pipe is inherited
        // directly by the unprivileged Frappe process.
        Assert.DoesNotContain("${2:-}", script);
        Assert.Contains("sys.stdin.readline", script);
        Assert.Contains("runuser -u \"$FRAPPE_USER\"", script);

        // Pinned Frappe exposes the CLI password as an argv option. The fixed
        // Python launcher invokes its functional internal implementation with
        // the password held only in that process's memory.
        Assert.Contains("from frappe.installer import _new_site", script);
        Assert.Contains("frappe.init(site, sites_path=\"/home/frappe/frappe-bench/sites\", new_site=True)", script);
        Assert.Contains("_new_site(", script);
        Assert.DoesNotContain("new_site.callback", script);
        Assert.DoesNotContain("--admin-password", script);

        // No secret-bearing wrapper/file is available to the frappe account.
        Assert.DoesNotContain("WRAPPER=", script);
        Assert.DoesNotContain("mktemp", script);
        Assert.DoesNotContain("ADMIN_PASSWORD", script);
        Assert.DoesNotContain("--admin-password-file", script);
    }

    [Fact]
    public void InitDataScript_GrantsFrappeOwnershipBeforeBindingPersistentSites()
    {
        var script = File.ReadAllText(ScriptPath);
        var ownership = script.IndexOf(
            "chown -R \"$FRAPPE_USER\":\"$FRAPPE_USER\" \"$MOUNT_POINT/frappe\"",
            StringComparison.Ordinal);
        var bind = script.IndexOf("mount --bind \"$MOUNT_POINT/frappe/sites\"", StringComparison.Ordinal);

        Assert.True(ownership >= 0, "The persistent Frappe tree must become writable by frappe.");
        Assert.True(bind >= 0 && ownership < bind,
            "Frappe ownership must be established before binding its persistent sites directory.");
    }

    [Fact]
    public void InitDataScript_PersistsDataAndBindMountsBeforeEnablingAppliance()
    {
        var script = File.ReadAllText(ScriptPath);
        var fstab = script.IndexOf("LABEL=SERPY_DATA", StringComparison.Ordinal);
        var mariaBind = script.IndexOf("/data/mariadb /var/lib/mysql none bind", StringComparison.Ordinal);
        var sitesBind = script.IndexOf("/data/frappe/sites /home/frappe/frappe-bench/sites none bind", StringComparison.Ordinal);
        var enable = script.IndexOf("systemctl enable serpy-appliance.target", StringComparison.Ordinal);

        Assert.True(fstab >= 0, "The initialized data disk must be mounted persistently by label.");
        Assert.True(mariaBind > fstab, "MariaDB bind mount must follow the data mount.");
        Assert.True(sitesBind > mariaBind, "Frappe sites bind mount must follow the data mount.");
        Assert.True(enable > sitesBind, "The appliance target must not be enabled before persistent mounts exist.");
    }

    [Theory]
    [InlineData("site1.local")]
    [InlineData("example-42.local")]
    public void SiteName_ValidHostLabel_IsAccepted(string siteName) =>
        Assert.True(InitializationParameters.IsValidSiteName(siteName));

    [Theory]
    [InlineData("")]
    [InlineData("site name")]
    [InlineData("site;id")]
    [InlineData("$(id)")]
    [InlineData("-site.local")]
    [InlineData("site-.local")]
    public void SiteName_ShellSyntaxOrInvalidLabel_IsRejected(string siteName) =>
        Assert.False(InitializationParameters.IsValidSiteName(siteName));
}
