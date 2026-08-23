using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serpy.Core.Configuration;

namespace Serpy.Core.Qemu;

/// <summary>
/// Generates and persists a per-install local CA and a client certificate.
/// QEMU is given the cert directory; Core loads the client cert for mTLS.
/// Both sides share the same CA; QEMU requires verify-peer=yes.
/// Called once at bundle install time; material lives in the protected per-user store.
/// </summary>
public sealed class TlsCertificateStore
{
    private readonly string _certDir;

    /// <summary>Directory containing ca-cert.pem, client-cert.pem, client-key.pem (used by Core).</summary>
    public string CoreCertDir => _certDir;

    /// <summary>
    /// Directory to pass to QEMU via -object tls-creds-x509,dir=...
    /// Contains ca-cert.pem, server-cert.pem, server-key.pem.
    /// </summary>
    public string QemuCertDir => Path.Combine(_certDir, "qemu");

    public TlsCertificateStore(string? certDir = null)
    {
        _certDir = certDir ?? Path.Combine(KnownPaths.SettingsDir, "certs");
    }

    /// <summary>
    /// Return true if all required certificate files already exist.
    /// </summary>
    public bool IsInitialized() =>
        File.Exists(Path.Combine(_certDir, "ca-cert.pem")) &&
        File.Exists(Path.Combine(_certDir, "client-cert.pem")) &&
        File.Exists(Path.Combine(_certDir, "client-key.pem")) &&
        File.Exists(Path.Combine(QemuCertDir, "ca-cert.pem")) &&
        File.Exists(Path.Combine(QemuCertDir, "server-cert.pem")) &&
        File.Exists(Path.Combine(QemuCertDir, "server-key.pem"));

    /// <summary>
    /// Generate a new local CA, server cert (for QEMU), and client cert (for Core).
    /// Existing material is overwritten.
    /// </summary>
    public void GenerateCertificates()
    {
        Directory.CreateDirectory(_certDir);
        Directory.CreateDirectory(QemuCertDir);

        // 1. Local CA
        using var caKey = RSA.Create(4096);
        var caReq = new CertificateRequest(
            "CN=SerпyLocalCA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        caReq.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        // 2. Server cert (for QEMU)
        using var serverKey = RSA.Create(4096);
        var serverReq = new CertificateRequest(
            "CN=SerпyQemuServer", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverReq.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        serverReq.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        // SAN: 127.0.0.1
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        serverReq.CertificateExtensions.Add(sanBuilder.Build());

        var serverCert = serverReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(9), [.. Guid.NewGuid().ToByteArray()]);

        // 3. Client cert (for Core)
        using var clientKey = RSA.Create(4096);
        var clientReq = new CertificateRequest(
            "CN=SerпyClient", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        clientReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        clientReq.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        clientReq.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false)); // clientAuth
        var clientCert = clientReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(9), [.. Guid.NewGuid().ToByteArray()]);

        // Write Core-side material
        WritePem(_certDir, "ca-cert.pem", caCert.ExportCertificatePem());
        WritePem(_certDir, "client-cert.pem", clientCert.ExportCertificatePem());
        WritePem(_certDir, "client-key.pem", clientKey.ExportRSAPrivateKeyPem());

        // Write QEMU-side material
        WritePem(QemuCertDir, "ca-cert.pem", caCert.ExportCertificatePem());
        WritePem(QemuCertDir, "server-cert.pem", serverCert.ExportCertificatePem());
        WritePem(QemuCertDir, "server-key.pem", serverKey.ExportRSAPrivateKeyPem());
    }

    /// <summary>Load the client certificate for use in mTLS connections.</summary>
    public X509Certificate2 LoadClientCert()
    {
        var certPem = File.ReadAllText(Path.Combine(_certDir, "client-cert.pem"));
        var keyPem = File.ReadAllText(Path.Combine(_certDir, "client-key.pem"));
        return X509Certificate2.CreateFromPem(certPem, keyPem);
    }

    /// <summary>Load the CA certificate for server validation.</summary>
    public X509Certificate2 LoadCaCert()
    {
        var pem = File.ReadAllText(Path.Combine(_certDir, "ca-cert.pem"));
        return X509Certificate2.CreateFromPem(pem);
    }

    private static void WritePem(string dir, string name, string content)
    {
        File.WriteAllText(Path.Combine(dir, name), content);
        // Restrict permissions on key files (best-effort on Windows).
        // Full ACL restriction is platform-specific and handled in U4/U7.
    }
}
