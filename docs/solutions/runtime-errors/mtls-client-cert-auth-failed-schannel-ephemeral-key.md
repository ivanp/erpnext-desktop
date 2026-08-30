---
title: mTLS client cert auth fails on Windows because CreateFromPem attaches an ephemeral key
date: 2026-08-24
category: runtime-errors
module: Serpy.Core
problem_type: runtime_error
component: authentication
severity: high
symptoms:
  - "Clicking 'Build Appliance' fails with 'Build failed: Authentication failed, see inner exception.'"
  - "SslStream client authentication throws System.Security.Authentication.AuthenticationException before any network I/O"
  - "SChannel rejects the ephemeral client cert key with Win32Exception: 'The credentials supplied to the package were not recognized'"
root_cause: wrong_api
resolution_type: code_fix
tags:
  - mtls
  - sslstream
  - schannel
  - x509certificate2
  - createfrompem
  - windows
  - qemu
  - client-cert
framework_version: dotnet 10.0
---

# mTLS client cert auth fails on Windows because CreateFromPem attaches an ephemeral key

## Problem

Clicking **Build Appliance** in the Serpy.App Windows host failed during the mTLS handshake to QEMU's serial chardev with `Build failed: Authentication failed, see inner exception.` The application did not surface the useful underlying exception to the user or build log.

## Symptoms

- The exact surfaced error was `Build failed: Authentication failed, see inner exception.`
- QEMU's stderr log contained no fatal crash or informative authentication error; it showed only benign deprecation and interrupt-vector warnings.
- The symptom was byte-for-byte identical before and after a separate, real WHPX/VMX fix: the `WHPX: Unexpected VP exit code 4` line disappeared, but the same authentication error persisted. That established that the WHPX crash fix was not the cause of this reported symptom.

## What Didn't Work

The first hypothesis was a WHPX/VMX QEMU failure. It looked plausible because `-cpu host` auto-enables `vmx=on`, while WHPX cannot virtualize VMX in this configuration; the resulting QEMU stderr included the well-documented-looking failure `WHPX: Unexpected VP exit code 4`. Disabling VMX passthrough by appending `,vmx=off` for WHPX was a real, necessary, independently verified fix: it removed the VP-exit crash from the log.

However, retrying after that fix left the reported `Authentication failed, see inner exception.` error completely unchanged. The crash line was gone, yet the authentication failure remained. This was evidence that the first fix addressed an independent defect rather than the reported symptom. Systematic debugging therefore returned to Phase 1 — reproducing and tracing the actual failure — instead of stacking a second guess on top of an unproven diagnosis.

## Solution

The root-cause tracing started with the error boundary. `BuildOperation.cs` used a catch-all that surfaced only `ex.Message`, swallowing the `InnerException` that identified the underlying failure. A throwaway console probe was written and then deleted; it was not committed. The probe launched the real installed QEMU 11.1.0 bundle with `-S` (paused, so guest boot state was irrelevant), supplied the same TLS serial-chardev arguments used by `BuildOperation`, ran the real `SerialClient.ConnectAsync`, and printed the complete exception chain and stack trace.

Per this session's verified investigation, the stack trace placed the failure inside `SslStream.AcquireClientCredentials` → `AcquireCredentialsHandle`. That isolated it to client-side credential acquisition before any network I/O reached the peer, rather than to QEMU boot, guest state, or a server-side TLS negotiation failure. The same probe tested the proposed PKCS#12 round-trip before production code was changed and confirmed that it succeeded.

In `src/Serpy.Core/Qemu/TlsCertificateStore.cs`, the implementation changed from:

```csharp
public X509Certificate2 LoadClientCert()
{
    var certPem = File.ReadAllText(Path.Combine(_certDir, "client-cert.pem"));
    var keyPem = File.ReadAllText(Path.Combine(_certDir, "client-key.pem"));
    return X509Certificate2.CreateFromPem(certPem, keyPem);
}
```

to:

```csharp
public X509Certificate2 LoadClientCert()
{
    var certPem = File.ReadAllText(Path.Combine(_certDir, "client-cert.pem"));
    var keyPem = File.ReadAllText(Path.Combine(_certDir, "client-key.pem"));
    using var ephemeral = X509Certificate2.CreateFromPem(certPem, keyPem);
    return X509CertificateLoader.LoadPkcs12(
        ephemeral.Export(X509ContentType.Pkcs12), password: null);
}
```

This is a single fix point covering all five production call sites — `BuildOperation`, `InitializeOperation`, `RecoverOperation`, `StartOperation`, and `StopOperation` — as well as the WHPX smoke tests, because each routes through `TlsCertificateStore.LoadClientCert()`.

## Why This Works

Per this session's verified investigation, on Windows, `SslStream` client-certificate authentication acquires credentials through SChannel's `AcquireCredentialsHandle`, which requires the private key to be backed by a CNG/CAPI key-store handle. `X509Certificate2.CreateFromPem` produced a certificate associated with an ephemeral, purely in-memory RSA key that had no such handle. SChannel rejected that key at credential-acquisition time with the Win32 error `The credentials supplied to the package were not recognized`; .NET then wrapped it as `AuthenticationException: Authentication failed, see inner exception.`

Because the rejection occurred before any TLS record was sent, the symptom looked like — and could easily be confused with — a server-side or network-level TLS failure. Exporting the certificate and key to PKCS#12 and reloading them with `X509CertificateLoader.LoadPkcs12` forced .NET to import the key through the Windows CNG key store, producing a certificate object that SChannel could use for client authentication. The probe confirmed this behavior against the real QEMU bundle on real hardware, and the production fix was verified there as well.

## Prevention

- Any code that loads an `X509Certificate2` from PEM for use as a TLS *client-authentication* certificate on Windows must round-trip it through PKCS#12, or otherwise ensure that its private key is CNG/CAPI-backed, before handing it to `SslStream` or `SslClientAuthenticationOptions.ClientCertificates`. Per this session's verified investigation, `CreateFromPem` alone produced an ephemeral key unsuitable for this SChannel client-authentication path; do not treat `HasPrivateKey` as sufficient evidence that the certificate is usable. The regression test is `tests/Serpy.Core.Tests/Qemu/TlsCertificateStoreTests.cs`, `LoadClientCert_AuthenticatesOverLoopbackMutualTls`. It exercises the real credential-acquisition path with a loopback `SslStream` server requiring client-certificate authentication, rather than merely checking certificate properties.
- When an exception says `see inner exception` but the catch site logs only `ex.Message`, treat that as a signal that the surfaced diagnostic is incomplete. Catch sites around cross-boundary protocol code such as TLS and native interop should log the full exception chain — `ex.ToString()` or an equivalent walk through `InnerException` — especially on production error paths visible to users.
- When a fix does not change the reported symptom, compare the before-and-after observable output byte-for-byte. Do not conclude that the fix solved the problem merely because a related warning disappeared; in this case, the VP-exit warning disappeared while the authentication message remained identical. Reproduce the remaining failure and trace it back to its actual boundary before making another change.

## Related Issues

- A separate, independently verified fix in the same debugging session and the same local commit as of this writing (not yet pushed to `origin/development`, so its hash is not a durable reference) disables WHPX VMX passthrough (`-cpu host,vmx=off`) to stop a `WHPX: Unexpected VP exit code 4` crash. It is a distinct root cause (a WHPX/VMX platform limitation, not a .NET/SChannel key-storage mismatch) and is a candidate for its own separate learning doc in a follow-up run — it is referenced here only because both fixes landed together in the same session.
