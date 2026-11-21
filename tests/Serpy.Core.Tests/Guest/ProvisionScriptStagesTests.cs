namespace Serpy.Core.Tests.Guest;

public sealed class ProvisionScriptStagesTests
{
    // The Installing screen lists these steps in this order (U9, U16). A stage added to or dropped
    // from the script without the screen would show the user a step list that is not what runs.
    private static readonly string[] ExpectedStages =
    [
        "os-packages", "database", "language-tools", "erpnext-source", "erpnext-assets", "services",
    ];

    [Fact]
    public void TheScriptEmitsTheSixStagesInTheOrderTheScreenShowsThem()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "guest", "provision.sh");
        var stages = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("stage ", StringComparison.Ordinal))
            .Where(l => !l.Contains("()", StringComparison.Ordinal)) // the function definition itself
            .Select(l => l["stage ".Length..].Trim())
            .ToArray();

        Assert.Equal(ExpectedStages, stages);
    }

    [Fact]
    public void TheMariaDbClientLibraryIsMariaDbs_NotOraclesLibmysqlclient()
    {
        // Measured on the first real Initialize: the Python mysqlclient module built against Oracle libmysqlclient
        // cannot load the mysql_native_password plugin that MariaDB accounts use, so no login works.
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "guest", "provision.sh"));
        var installLine = script.Split('\n').Single(l => l.Contains("apt-get install -y git cron", StringComparison.Ordinal));

        Assert.Contains("libmariadb-dev", installLine, StringComparison.Ordinal);
        Assert.Contains("libmariadb-dev-compat", installLine, StringComparison.Ordinal);
        Assert.DoesNotContain("libmysqlclient-dev", installLine, StringComparison.Ordinal);
    }
}
