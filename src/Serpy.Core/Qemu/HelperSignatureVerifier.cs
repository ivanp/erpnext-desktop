using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Serpy.Core.Qemu;

/// <summary>Result of verifying a binary's Authenticode signature and publisher (IR7).</summary>
public sealed record SignatureVerificationResult(
    bool ChainIsTrusted,
    bool SubjectMatches,
    string? ActualSubject,
    string Message)
{
    /// <summary>True only when the chain verifies AND the subject matches the expected publisher.</summary>
    public bool IsValid => ChainIsTrusted && SubjectMatches;
}

/// <summary>
/// Verifies a binary's Authenticode signature via the real Win32 <c>WinVerifyTrust</c>
/// API (the same trust-chain evaluation <c>signtool verify</c> and
/// <c>Get-AuthenticodeSignature</c> use) — not a shortcut or a hand-rolled parser.
///
/// Used on the app side, before elevating <c>Serpy.InstallerBootstrapper.exe</c>
/// (IR7): an unsigned, tampered, or wrong-publisher binary must fail closed with
/// no elevation attempted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HelperSignatureVerifier
{
    /// <summary>
    /// Verify <paramref name="filePath"/> is Authenticode-signed by a chain-trusted
    /// certificate whose subject exactly matches <paramref name="expectedSubject"/>.
    /// Never throws for a normal unsigned/tampered/wrong-publisher file — those are
    /// reported via <see cref="SignatureVerificationResult.IsValid"/> = false.
    /// </summary>
    public static SignatureVerificationResult Verify(string filePath, string expectedSubject)
    {
        if (!File.Exists(filePath))
            return new SignatureVerificationResult(false, false, null, $"File does not exist: {filePath}");

        var chainIsTrusted = VerifyTrustChain(filePath, out var trustMessage);

        string? actualSubject = null;
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile extracts the embedded cert regardless of chain trust; intentional.
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            actualSubject = cert.Subject;
        }
        catch
        {
            // No embedded certificate to extract (e.g. genuinely unsigned file).
        }

        var subjectMatches = actualSubject is not null &&
            string.Equals(actualSubject, expectedSubject, StringComparison.Ordinal);

        return new SignatureVerificationResult(chainIsTrusted, subjectMatches, actualSubject, trustMessage);
    }

    // ── WinVerifyTrust P/Invoke (WINTRUST_ACTION_GENERIC_VERIFY_V2) ──────────────

    private static bool VerifyTrustChain(string filePath, out string message)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var trustData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WTD_SAFER_FLAG | WTD_REVOCATION_CHECK_NONE,
                dwUIContext = 0,
            };

            var actionGuid = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(IntPtr.Zero, ref actionGuid, ref trustData);

            // Always release the trust provider's state, even on failure.
            trustData.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref actionGuid, ref trustData);

            message = result switch
            {
                0 => "Signature verified; chain trusted.",
                TRUST_E_NOSIGNATURE => "File is not signed.",
                TRUST_E_BAD_DIGEST => "Signature present but file content does not match (tampered).",
                TRUST_E_EXPLICIT_DISTRUST => "Publisher explicitly distrusted.",
                CERT_E_UNTRUSTEDROOT => "Certificate chain terminates in an untrusted root.",
                CERT_E_CHAINING => "A certificate chain could not be built.",
                _ => $"WinVerifyTrust returned 0x{result:X8}.",
            };
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private const int CERT_E_CHAINING = unchecked((int)0x800B010A);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
