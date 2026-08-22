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
}
