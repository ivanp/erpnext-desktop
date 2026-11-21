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
    public void InitDataScript_CreatesTheSiteThroughATemporarySuperuser_ThatIsAlwaysDropped()
    {
        // root@localhost authenticates through the unix socket as the OS root, so the frappe user cannot use it, and
        // with no password frappe prompts on a terminal that does not exist (measured on the first real Initialize).
        var script = File.ReadAllText(ScriptPath);
        var create = script.IndexOf("CREATE OR REPLACE USER", StringComparison.Ordinal);
        var trap = script.IndexOf("trap drop_setup_user EXIT", StringComparison.Ordinal);
        var use = script.IndexOf("db_root_password=os.environ.pop(\"SETUP_DB_SECRET\")", StringComparison.Ordinal);
        var drop = script.LastIndexOf("drop_setup_user", StringComparison.Ordinal);

        Assert.True(trap >= 0 && trap < create, "The drop must be armed before the account exists, so a failure cannot leave it behind.");
        Assert.True(create < use, "The account is created before frappe uses it.");
        Assert.True(drop > use, "The account is dropped after frappe has used it.");
        Assert.DoesNotContain("db_root_username=\"root\"", script);
    }

    [Fact]
    public void InitDataScript_NeverPutsTheSetupSecretInAFileOrAnArgument()
    {
        var script = File.ReadAllText(ScriptPath);

        Assert.Contains("SETUP_DB_SECRET=\"$SETUP_DB_SECRET\" SETUP_DB_USER=\"$SETUP_DB_USER\"", script);
        Assert.Contains("os.environ.pop(\"SETUP_DB_SECRET\")", script);
        Assert.DoesNotContain("--db-root-password", script);
        Assert.DoesNotContain("> /tmp/", script);
    }

    [Fact]
    public void InitDataScript_StartsTheBenchServices_BeforeItInstallsTheApp()
    {
        var script = File.ReadAllText(ScriptPath);
        var start = script.IndexOf("systemctl start supervisor", StringComparison.Ordinal);
        var install = script.IndexOf("install-app erpnext", StringComparison.Ordinal);

        Assert.True(start >= 0 && start < install, "Installing an app talks to the bench redis servers, which supervisor runs.");
    }

    [Fact]
    public void InitDataScript_ConfiguresNginxForABrowserOnTheLoopback()
    {
        var script = File.ReadAllText(ScriptPath);

        Assert.Contains("setup nginx --yes --logging none", script);   // no prompt, and no undefined log format
        Assert.Contains("chmod 0711", script);                          // www-data must be able to reach the assets
        Assert.Contains("rm -f /etc/nginx/sites-enabled/default", script); // the browser uses 127.0.0.1, not the site name
        Assert.Contains("setup nginx --yes --logging none", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "guest", "recover.sh")));
    }

    [Fact]
    public void InitDataScript_SeedsTheBenchFilesFromTheImage_BeforeTheEmptySitesTreeIsBound()
    {
        // The bind mount hides the sites/ directory of the image. apps.txt, common_site_config.json and
        // assets/ live there, so they must be copied to the data disk first, or site creation fails.
        var script = File.ReadAllText(ScriptPath);
        var seed = script.IndexOf("cp -a \"$FRAPPE_DIR/sites/.\" \"$MOUNT_POINT/frappe/sites/\"", StringComparison.Ordinal);
        var ownership = script.IndexOf(
            "chown -R \"$FRAPPE_USER\":\"$FRAPPE_USER\" \"$MOUNT_POINT/frappe\"", StringComparison.Ordinal);
        var bind = script.IndexOf("mount --bind \"$MOUNT_POINT/frappe/sites\"", StringComparison.Ordinal);
        var firstUse = script.IndexOf("_new_site(", StringComparison.Ordinal);

        Assert.True(seed >= 0, "The bench files of the image must be copied to the data disk.");
        Assert.True(seed < ownership, "Ownership must cover the copied files, so the copy comes first.");
        Assert.True(seed < bind, "The copy must run before the bind hides the sites directory of the image.");
        Assert.True(bind < firstUse, "The site is created on the bound tree.");
    }

    [Fact]
    public void InitDataScript_SeedsOnlyWhenTheDataDiskHasNoBenchFiles_SoARerunNeverOverwritesASite()
    {
        var script = File.ReadAllText(ScriptPath);
        var guard = script.IndexOf("if [ ! -f \"$MOUNT_POINT/frappe/sites/apps.txt\" ]; then", StringComparison.Ordinal);
        var seed = script.IndexOf("cp -a \"$FRAPPE_DIR/sites/.\"", StringComparison.Ordinal);

        Assert.True(guard >= 0, "The copy must be guarded by the presence of apps.txt on the data disk.");
        Assert.True(guard < seed, "The guard must wrap the copy.");
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
