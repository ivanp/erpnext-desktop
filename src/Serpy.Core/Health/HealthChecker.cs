using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serpy.Core.Guest;

namespace Serpy.Core.Health;

/// <summary>
/// R11 functional health check — proves ERPNext is actually working.
/// A rendered login page alone does NOT pass (AE2).
///
/// Checks in order (first failure reported):
///   1. bench list-apps shows erpnext installed
///   2. Scheduler active
///   3. Authenticated REST request succeeds
///   4. Create → read → delete a disposable ToDo record through the app layer
/// </summary>
public sealed class HealthChecker(
    GuestOperations guest,
    string siteName,
    string loopbackUrl,
    HealthCredentials credentials)
{
    public async Task<HealthResult> RunAsync(CancellationToken ct = default)
    {
        // 1. Apps
        try
        {
            var apps = await guest.ListAppsAsync(siteName, ct);
            if (!apps.Contains("erpnext", StringComparison.OrdinalIgnoreCase))
                return HealthResult.Fail("ERPNext not listed in bench list-apps", "apps");
        }
        catch (Exception ex)
        {
            return HealthResult.Fail($"bench list-apps failed: {ex.Message}", "apps");
        }

        // 2. Scheduler
        try
        {
            if (!await guest.IsSchedulerRunningAsync(siteName, ct))
                return HealthResult.Fail("Scheduler is not active", "scheduler");
        }
        catch (Exception ex)
        {
            return HealthResult.Fail($"Scheduler check failed: {ex.Message}", "scheduler");
        }

        // 3 + 4. Authenticated REST + CRUD
        var cred = credentials.Retrieve();
        if (cred is null)
            return HealthResult.Fail(
                "No admin credential stored — cannot perform authenticated health check", "auth");

        try
        {
            var cookie = await GetSessionCookieAsync(cred.Value.AdminPassword, ct);
            if (cookie is null)
                return HealthResult.Fail("Authentication failed — check admin credential", "auth");

            var docName = await CreateProbeRecordAsync(cookie, ct);
            if (docName is null)
                return HealthResult.Fail("Could not create probe ToDo record", "crud-create");

            if (!await ReadProbeRecordAsync(cookie, docName, ct))
                return HealthResult.Fail($"Could not read probe record {docName}", "crud-read");

            await DeleteProbeRecordAsync(cookie, docName, ct);
        }
        catch (Exception ex)
        {
            return HealthResult.Fail($"Authenticated check failed: {ex.Message}", "auth");
        }

        return HealthResult.Ok();
    }

    // ── REST helpers ──────────────────────────────────────────────────────────

    private async Task<string?> GetSessionCookieAsync(string password, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(loopbackUrl) };
        var body = new StringContent(
            JsonSerializer.Serialize(
                new LoginRequest { Usr = "Administrator", Pwd = password },
                HealthJsonContext.Default.LoginRequest),
            Encoding.UTF8, "application/json");

        var resp = await http.PostAsync("/api/method/login", body, ct);
        if (!resp.IsSuccessStatusCode) return null;

        return resp.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies)
            : null;
    }

    private async Task<string?> CreateProbeRecordAsync(string cookie, CancellationToken ct)
    {
        using var http = MakeClient(cookie);
        var body = new StringContent(
            JsonSerializer.Serialize(
                new CreateToDoRequest { Subject = "Serpy-health-probe", Status = "Open" },
                HealthJsonContext.Default.CreateToDoRequest),
            Encoding.UTF8, "application/json");

        var resp = await http.PostAsync("/api/resource/ToDo", body, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc  = JsonSerializer.Deserialize(json, HealthJsonContext.Default.FrappeDocResponse);
        return doc?.Data?.Name;
    }

    private async Task<bool> ReadProbeRecordAsync(string cookie, string name, CancellationToken ct)
    {
        using var http = MakeClient(cookie);
        var resp = await http.GetAsync($"/api/resource/ToDo/{Uri.EscapeDataString(name)}", ct);
        return resp.IsSuccessStatusCode;
    }

    private async Task DeleteProbeRecordAsync(string cookie, string name, CancellationToken ct)
    {
        using var http = MakeClient(cookie);
        await http.DeleteAsync($"/api/resource/ToDo/{Uri.EscapeDataString(name)}", ct);
    }

    private HttpClient MakeClient(string cookie)
    {
        var http = new HttpClient { BaseAddress = new Uri(loopbackUrl) };
        http.DefaultRequestHeaders.Add("Cookie", cookie);
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}

public sealed record HealthResult(bool Healthy, string? FailedCheck, string? Reason)
{
    public static HealthResult Ok() => new(true, null, null);
    public static HealthResult Fail(string reason, string check) => new(false, check, reason);
}
