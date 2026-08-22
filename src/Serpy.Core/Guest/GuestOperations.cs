using Serpy.Core.Protocols.Qga;

namespace Serpy.Core.Guest;

/// <summary>
/// Typed wrappers around QGA guest-exec calls.
/// All in-guest operations flow through this class — no ad-hoc exec strings elsewhere.
/// Credentials are never passed as command-line arguments; use stdin (InputData) instead.
/// </summary>
public sealed class GuestOperations(QgaClient qga)
{
    // ── Version queries ───────────────────────────────────────────────────────

    public Task<string> GetPythonVersionAsync(CancellationToken ct = default) =>
        RunOutputAsync("python3", ["--version"], ct);

    public Task<string> GetNodeVersionAsync(CancellationToken ct = default) =>
        RunOutputAsync("node", ["--version"], ct);

    public Task<string> GetMariaDbVersionAsync(CancellationToken ct = default) =>
        RunOutputAsync("mariadb", ["--version"], ct);

    public Task<string> GetRedisVersionAsync(CancellationToken ct = default) =>
        RunOutputAsync("redis-server", ["--version"], ct);

    // ── Data initialization ───────────────────────────────────────────────────

    /// <summary>
    /// Run the baked init-data.sh helper on the guest (R14, KD4).
    /// The admin password is passed exclusively via QGA stdin (InputData);
    /// it never appears in command-line arguments, environment strings, or
    /// QGA-captured args. The helper passes the inherited pipe in memory to
    /// Frappe's pinned internal site-creation API.
    /// </summary>
    public async Task<GuestExecResult> RunInitDataAsync(
        string siteName,
        string adminPassword,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        return await qga.ExecAsync(
            "/usr/local/bin/init-data.sh",
            args: [siteName],          // only the site name — never the password
            stdin: adminPassword,       // password via stdin only
            timeout: timeout,
            ct: ct);
    }

    // ── Recovery ──────────────────────────────────────────────────────────────

    public async Task<GuestExecResult> RunRecoverAsync(
        string siteName,
        TimeSpan timeout,
        CancellationToken ct = default) =>
        await qga.ExecAsync(
            "/usr/local/bin/recover.sh",
            args: [siteName],
            timeout: timeout,
            ct: ct);

    // ── Disk boundary probes ──────────────────────────────────────────────────

    /// <summary>
    /// Query MariaDB @@datadir. Returns the path MariaDB is actually using.
    /// Used by the physical-location assertion (R5, AE2).
    /// </summary>
    public async Task<string> GetMariaDbDatadirAsync(CancellationToken ct = default)
    {
        var r = await qga.ExecAsync(
            "mariadb",
            args: ["--execute", "SELECT @@datadir;", "--batch", "--skip-column-names"],
            ct: ct);
        return r.Stdout.Trim();
    }

    /// <summary>
    /// Check whether a path is a mount point in the guest.
    /// </summary>
    public async Task<bool> IsMountedAsync(string path, CancellationToken ct = default)
    {
        var r = await qga.ExecAsync(
            "mountpoint", args: ["-q", path], ct: ct);
        return r.Succeeded;
    }

    /// <summary>
    /// Query Frappe version via `bench version frappe`.
    /// Returns empty string if bench is not available yet.
    /// </summary>
    public async Task<string> GetFrappeVersionAsync(CancellationToken ct = default)
    {
        try
        {
            return await RunOutputAsync(
                "su", ["-", "frappe", "-c",
                    "cd /home/frappe/frappe-bench && bench version frappe"],
                ct);
        }
        catch { return string.Empty; }
    }

    /// <summary>Query ERPNext version via `bench version erpnext`.</summary>
    public async Task<string> GetErpNextVersionAsync(CancellationToken ct = default)
    {
        try
        {
            return await RunOutputAsync(
                "su", ["-", "frappe", "-c",
                    "cd /home/frappe/frappe-bench && bench version erpnext"],
                ct);
        }
        catch { return string.Empty; }
    }

    // ── Health sub-checks (R11) ───────────────────────────────────────────────

    /// <summary>List installed bench apps for a site.</summary>
    public async Task<string> ListAppsAsync(string siteName, CancellationToken ct = default) =>
        await RunOutputAsync(
            "su", ["-", "frappe", "-c",
                $"cd /home/frappe/frappe-bench && bench --site {siteName} list-apps"],
            ct);

    /// <summary>Check scheduler is running.</summary>
    public async Task<bool> IsSchedulerRunningAsync(string siteName, CancellationToken ct = default)
    {
        var r = await qga.ExecAsync(
            "su", ["-", "frappe", "-c",
                $"cd /home/frappe/frappe-bench && bench --site {siteName} scheduler status"],
            ct: ct);
        return r.Succeeded &&
               r.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string> RunOutputAsync(
        string cmd, string[] args, CancellationToken ct)
    {
        var r = await qga.ExecAsync(cmd, args, ct: ct);
        if (!r.Succeeded)
            throw new InvalidOperationException(
                $"Guest command failed: {cmd} → exit {r.ExitCode}: {r.Stderr}");
        return r.Stdout.Trim();
    }
}
