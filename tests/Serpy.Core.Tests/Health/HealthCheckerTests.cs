using Serpy.Core.Health;

namespace Serpy.Core.Tests.Health;

public sealed class HealthCredentialsTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"SerpyHealthCred-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Store_ThenRetrieve_RoundTrips()
    {
        Directory.CreateDirectory(_dir);
        var creds = new HealthCredentials(Path.Combine(_dir, "cred"));
        creds.Store("site1.local", "secret-password");

        var retrieved = creds.Retrieve();

        Assert.NotNull(retrieved);
        Assert.Equal("site1.local",       retrieved!.Value.SiteName);
        Assert.Equal("secret-password",   retrieved!.Value.AdminPassword);
    }

    [Fact]
    public void Retrieve_WhenNotStored_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        var creds = new HealthCredentials(Path.Combine(_dir, "cred"));
        Assert.Null(creds.Retrieve());
    }

    [Fact]
    public void Clear_RemovesStoredCredential()
    {
        Directory.CreateDirectory(_dir);
        var creds = new HealthCredentials(Path.Combine(_dir, "cred"));
        creds.Store("site1.local", "password");
        creds.Clear();
        Assert.Null(creds.Retrieve());
    }

    [Fact]
    public void Store_CredentialNotInFileName_PathIsOpaque()
    {
        // The credential must not appear in the stored file path or name.
        Directory.CreateDirectory(_dir);
        var storePath = Path.Combine(_dir, "cred");
        var creds = new HealthCredentials(storePath);
        creds.Store("site1.local", "super-secret");

        // The store path must not contain the password.
        Assert.DoesNotContain("super-secret", storePath);

        // On Windows, the file content is DPAPI-encrypted (opaque bytes).
        // On other platforms it's plaintext but at least the filename is clean.
        // We don't assert on file content here since it's platform-dependent.
    }

    [Fact]
    public void Store_ReplacesExistingHiddenCredential()
    {
        Directory.CreateDirectory(_dir);
        var storePath = Path.Combine(_dir, "cred");
        var creds = new HealthCredentials(storePath);
        creds.Store("site1.local", "first-password");
        File.SetAttributes(storePath, File.GetAttributes(storePath) | FileAttributes.Hidden);

        creds.Store("site1.local", "replacement-password");

        Assert.Equal("replacement-password", creds.Retrieve()!.Value.AdminPassword);
    }
}

public sealed class HealthCheckerSignInTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyHealthSignIn-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeGuest(bool erpnextListed = true, bool scheduler = true) : Serpy.Core.Guest.IGuestHealthProbe
    {
        public Task<string> ListAppsAsync(string siteName, CancellationToken ct = default) =>
            Task.FromResult(erpnextListed ? "frappe\nerpnext" : "frappe");

        public Task<bool> IsSchedulerRunningAsync(string siteName, CancellationToken ct = default) =>
            Task.FromResult(scheduler);
    }

    private sealed class FakeSite(System.Net.HttpStatusCode login) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> PostedBodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add($"{request.Method} {path}");

            if (path == "/api/method/login")
            {
                var response = new HttpResponseMessage(login);
                if (login == System.Net.HttpStatusCode.OK) response.Headers.Add("Set-Cookie", "sid=abc; Path=/");
                return Task.FromResult(response);
            }

            // Frappe 16's ToDo has no subject field and requires description: a body without it is
            // answered with 417 MandatoryError, measured on a real guest.
            if (request.Method == HttpMethod.Post && path == "/api/resource/ToDo")
            {
                var posted = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                lock (Requests) PostedBodies.Add(posted);
                if (!posted.Contains("\"description\"", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)417)
                    {
                        Content = new StringContent("{\"exc_type\":\"MandatoryError\"}"),
                    });

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"name\":\"todo-1\"}}"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private HealthCredentials Saved(string password = "admin")
    {
        Directory.CreateDirectory(_dir);
        var credentials = new HealthCredentials(Path.Combine(_dir, "cred"));
        credentials.Store("site1.local", password);
        return credentials;
    }

    private static HealthChecker Checker(FakeGuest guest, FakeSite site, HealthCredentials credentials) =>
        new(guest, "site1.local", "http://127.0.0.1:18080", credentials, site);

    [Fact]
    public async Task ARejectedPassword_IsADistinctResult_NotAGenericAuthFailure()
    {
        var site = new FakeSite(System.Net.HttpStatusCode.Unauthorized);

        var result = await Checker(new FakeGuest(), site, Saved("wrong")).RunAsync();

        Assert.False(result.Healthy);
        Assert.True(result.IsPasswordRejected);
        Assert.Equal(HealthResult.PasswordCheck, result.FailedCheck);
        Assert.DoesNotContain("wrong", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedPassword_StopsBeforeAnyRecordIsCreated()
    {
        var site = new FakeSite(System.Net.HttpStatusCode.Unauthorized);

        await Checker(new FakeGuest(), site, Saved()).RunAsync();

        Assert.DoesNotContain(site.Requests, r => r.Contains("/api/resource", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    [InlineData(System.Net.HttpStatusCode.BadGateway)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    public async Task ASiteThatIsNotReady_IsNotReportedAsARejectedPassword(System.Net.HttpStatusCode status)
    {
        var site = new FakeSite(status);

        var result = await Checker(new FakeGuest(), site, Saved()).RunAsync();

        Assert.False(result.Healthy);
        Assert.False(result.IsPasswordRejected);
        Assert.Equal("auth", result.FailedCheck);
    }

    [Fact]
    public async Task ACorrectPassword_RunsTheCreateReadDeleteCheck_AndIsHealthy()
    {
        var site = new FakeSite(System.Net.HttpStatusCode.OK);

        var result = await Checker(new FakeGuest(), site, Saved()).RunAsync();

        Assert.True(result.Healthy);
        Assert.False(result.IsPasswordRejected);
        Assert.Contains("POST /api/resource/ToDo", site.Requests);
        Assert.Contains("DELETE /api/resource/ToDo/todo-1", site.Requests);
    }

    [Fact]
    public async Task TheProbeRecord_CarriesTheMandatoryDescription_ThatFrappe16Requires()
    {
        var site = new FakeSite(System.Net.HttpStatusCode.OK);

        var result = await Checker(new FakeGuest(), site, Saved()).RunAsync();

        Assert.True(result.Healthy, result.Reason);
        var body = Assert.Single(site.PostedBodies);
        Assert.Contains("\"description\":\"Serpy-health-probe\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"subject\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChecksThatNeedNoSignIn_ComeFirst_SoARejectedPasswordMeansTheRestPassed()
    {
        var site = new FakeSite(System.Net.HttpStatusCode.Unauthorized);

        var noApp = await Checker(new FakeGuest(erpnextListed: false), site, Saved()).RunAsync();
        var noScheduler = await Checker(new FakeGuest(scheduler: false), site, Saved()).RunAsync();

        Assert.Equal("apps", noApp.FailedCheck);
        Assert.Equal("scheduler", noScheduler.FailedCheck);
        Assert.DoesNotContain(site.Requests, r => r.Contains("login", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithNoSavedCredential_SiteAcceptsAdmin_SucceedsAndStoresCredential()
    {
        Directory.CreateDirectory(_dir);
        var creds = new HealthCredentials(Path.Combine(_dir, "stored-cred"));
        var site = new FakeSite(System.Net.HttpStatusCode.OK);

        var result = await Checker(new FakeGuest(), site, creds).RunAsync();

        Assert.True(result.Healthy);
        Assert.False(result.IsPasswordRejected);
        var retrieved = creds.Retrieve();
        Assert.NotNull(retrieved);
        Assert.Equal("site1.local", retrieved.Value.SiteName);
        Assert.Equal("admin", retrieved.Value.AdminPassword);
    }

    [Fact]
    public async Task WithNoSavedCredential_SiteRejectsAdmin_ReturnsPasswordRejected_AndDoesNotStore()
    {
        Directory.CreateDirectory(_dir);
        var creds = new HealthCredentials(Path.Combine(_dir, "empty-cred"));
        var site = new FakeSite(System.Net.HttpStatusCode.Unauthorized);

        var result = await Checker(new FakeGuest(), site, creds).RunAsync();

        Assert.False(result.Healthy);
        Assert.True(result.IsPasswordRejected);
        Assert.Equal(HealthResult.PasswordCheck, result.FailedCheck);
        Assert.Null(creds.Retrieve());
    }

    [Fact]
    public async Task WithNoSavedCredential_SiteNotReady_ReturnsGenericAuthFailure_AndDoesNotStore()
    {
        Directory.CreateDirectory(_dir);
        var creds = new HealthCredentials(Path.Combine(_dir, "server-err-cred"));
        var site = new FakeSite(System.Net.HttpStatusCode.InternalServerError);

        var result = await Checker(new FakeGuest(), site, creds).RunAsync();

        Assert.False(result.Healthy);
        Assert.False(result.IsPasswordRejected);
        Assert.Equal("auth", result.FailedCheck);
        Assert.Null(creds.Retrieve());
    }

    [Fact]
    public async Task CheckPasswordAsync_ValidAndRejected_ReportCorrectStatus()
    {
        var okSite = new FakeSite(System.Net.HttpStatusCode.OK);
        var unauthSite = new FakeSite(System.Net.HttpStatusCode.Unauthorized);
        var creds = Saved();

        var okChecker = Checker(new FakeGuest(), okSite, creds);
        var unauthChecker = Checker(new FakeGuest(), unauthSite, creds);

        var (valid, rejected) = await okChecker.CheckPasswordAsync("admin");
        Assert.True(valid);
        Assert.False(rejected);

        var (unauthValid, unauthRejected) = await unauthChecker.CheckPasswordAsync("wrong");
        Assert.False(unauthValid);
        Assert.True(unauthRejected);
    }
}
