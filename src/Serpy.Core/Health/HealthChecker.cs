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
    IGuestHealthProbe guest,
    string siteName,
    string loopbackUrl,
    HealthCredentials credentials,
    HttpMessageHandler? httpHandler = null)
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
        bool fallbackAdmin = false;
        string password;
        if (cred is not null)
        {
            password = cred.Value.AdminPassword;
        }
        else
        {
            fallbackAdmin = true;
            password = "admin";
        }

        try
        {
            var login = await SignInAsync(password, ct);
            if (login.Rejected)
                return HealthResult.PasswordRejected(
                    fallbackAdmin
                        ? "ERPNext rejected the Administrator password."
                        : "ERPNext rejected the saved Administrator password.");
            var cookie = login.Cookie;
            if (cookie is null)
                return HealthResult.Fail("Authentication failed — check admin credential", "auth");

            var docName = await CreateProbeRecordAsync(cookie, ct);
            if (docName is null)
                return HealthResult.Fail("Could not create probe ToDo record", "crud-create");

            if (!await ReadProbeRecordAsync(cookie, docName, ct))
                return HealthResult.Fail($"Could not read probe record {docName}", "crud-read");

            await DeleteProbeRecordAsync(cookie, docName, ct);

            if (fallbackAdmin)
            {
                credentials.Store(siteName, "admin");
            }
        }
        catch (Exception ex)
        {
            return HealthResult.Fail($"Authenticated check failed: {ex.Message}", "auth");
        }

        return HealthResult.Ok();
    }
    /// <summary>
    /// Verifies candidate password credentials against Frappe's login method without persisting (KTD12).
    /// </summary>
    public async Task<(bool Valid, bool Rejected)> CheckPasswordAsync(string password, CancellationToken ct = default)
    {
        var login = await SignInAsync(password, ct);
        return (login.Cookie is not null, login.Rejected);
    }


    // ── REST helpers ──────────────────────────────────────────────────────────

    // Frappe answers a wrong password with 401, and a login that needs a second step with 403 or 417.
    // Only a 401 is a rejected password. Any other failure (a 5xx, a refused connection) says the
    // site is not ready, which is a different problem with a different remedy.
    private async Task<(string? Cookie, bool Rejected)> SignInAsync(string password, CancellationToken ct)
    {
        using var http = NewClient();
        var body = new StringContent(
            JsonSerializer.Serialize(
                new LoginRequest { Usr = "Administrator", Pwd = password },
                HealthJsonContext.Default.LoginRequest),
            Encoding.UTF8, "application/json");

        var resp = await http.PostAsync("/api/method/login", body, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (null, true);
        if (!resp.IsSuccessStatusCode) return (null, false);

        var cookie = resp.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies)
            : null;
        return (cookie, false);
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

    private HttpClient NewClient() => httpHandler is null
        ? new HttpClient { BaseAddress = new Uri(loopbackUrl) }
        : new HttpClient(httpHandler, disposeHandler: false) { BaseAddress = new Uri(loopbackUrl) };

    private HttpClient MakeClient(string cookie)
    {
        var http = NewClient();
        http.DefaultRequestHeaders.Add("Cookie", cookie);
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }
}

public sealed record HealthResult(bool Healthy, string? FailedCheck, string? Reason)
{
    /// <summary>The check that reports a rejected sign-in (KTD12). It is not a generic failure.</summary>
    public const string PasswordCheck = "password";

    public static HealthResult Ok() => new(true, null, null);
    public static HealthResult Fail(string reason, string check) => new(false, check, reason);

    /// <summary>
    /// Everything that needs no sign-in passed and ERPNext refused the password. The site is up, so
    /// start can leave it running and ask for the password, in place of stopping it (KTD12).
    /// </summary>
    public static HealthResult PasswordRejected(string reason) => new(false, PasswordCheck, reason);

    public bool IsPasswordRejected => !Healthy && FailedCheck == PasswordCheck;
}
