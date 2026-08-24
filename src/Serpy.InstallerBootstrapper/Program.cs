using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Serpy.Core.Qemu;

// ── Serpy.InstallerBootstrapper ──────────────────────────────────────────────
// The separate, signed, elevated-only executable (IR3/IKTD1). Never linked
// into Serpy.App.exe; launched only via `runas` by
// ManagedRuntimeResolver.DefaultBootstrapLauncher (IU1 step 3), which passes
// the four bootstrap arguments below and then connects to the pipe this
// process hosts as CLIENT (see ElevatedInstallChannel).
//
// This process is the pipe SERVER precisely so it can impersonate the
// connecting client and read its *real* token SID (step 7) -- the app cannot
// be trusted to self-report its own identity, since `runas` routes through
// the AppInfo/consent service and this process's parent is not reliably the
// app that requested elevation.

const int ExitBadArguments = 1;
const int ExitInvalidSource = 2;
const int ExitPipeFailure = 3;
const int ExitAuthFailed = 4;
const int ExitDescriptorInvalid = 5;
const int ExitShaMismatch = 6;
const int ExitReparsePointDetected = 7;
const int ExitVendorLaunchFailed = 8;

var parsedArgs = ParseArgs(args);
if (!parsedArgs.TryGetValue("pipe-name", out var pipeName) ||
    !parsedArgs.TryGetValue("nonce", out var expectedNonce) ||
    !parsedArgs.TryGetValue("claimed-sid", out var claimedSidValue) ||
    !parsedArgs.TryGetValue("installer-path", out var installerPath))
{
    await Console.Error.WriteLineAsync(
        "Usage: Serpy.InstallerBootstrapper.exe --pipe-name <name> --nonce <hex> " +
        "--claimed-sid <sid> --installer-path <path>");
    return ExitBadArguments;
}

// Reject a malformed/foreign SID string before it can be used to build a
// pipe DACL -- SecurityIdentifier's constructor throws ArgumentException for
// a non-SID string, which is exactly the "abort before touching anything
// dangerous" behavior wanted here.
SecurityIdentifier claimedSid;
try
{
    claimedSid = new SecurityIdentifier(claimedSidValue);
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync($"Invalid --claimed-sid: {ex.Message}");
    return ExitBadArguments;
}

// Validate the source file BEFORE creating the pipe or accepting a
// connection: a reparse point/UNC/missing source is rejected up front,
// mirroring BootstrapProtocol.ValidateSourceFile's existing contract.
try
{
    BootstrapProtocol.ValidateSourceFile(installerPath);
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return ExitInvalidSource;
}

// ── Pipe server, DACL-restricted to the claimed SID only ───────────────────
// Built via a direct CreateNamedPipeW P/Invoke (see NativePipe below) rather
// than the higher-level PipeSecurity/NamedPipeServerStreamAcl wrapper: only
// the claimed SID is granted read/write access to connect; every other
// principal -- including other non-admin users on the same machine -- is
// denied by omission (an empty DACL apart from that one explicit rule).
using SafePipeHandle rawHandle = NativePipe.CreateDaclRestricted(pipeName, claimedSid);
await using var server = new NamedPipeServerStream(
    PipeDirection.InOut, isAsync: true, isConnected: false, rawHandle);

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

try
{
    await server.WaitForConnectionAsync(cts.Token);
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync(
        "No client connected to the bootstrap pipe within the timeout. " +
        "No elevated action was taken.");
    return ExitPipeFailure;
}

// Read the client's hello message BEFORE attempting impersonation: Win32
// requires at least one successful read/write to have occurred on the pipe
// before ImpersonateNamedPipeClient can succeed (NamedPipeServerStream.RunAsClient
// throws "Unable to impersonate ... until data has been read" otherwise) --
// reading is plain I/O and does not itself require impersonation.
var helloLine = await ReadLineAsync(server, cts.Token);
BootstrapClientHello hello;
try
{
    hello = JsonSerializer.Deserialize(helloLine, BootstrapWireJsonContext.Default.BootstrapClientHello)
        ?? throw new InvalidDataException("Empty hello message.");
}
catch (JsonException ex)
{
    await WriteFailureResultAsync(server, $"Malformed hello message: {ex.Message}", cts.Token);
    return ExitAuthFailed;
}

// ── Authenticate the connection: impersonate the client, read its REAL SID ──
// (step 7). The bootstrap arguments (claimed SID, nonce) are only ever
// validated against this authenticated identity -- never trusted on their
// own. RunAsClient wraps the Win32 ImpersonateNamedPipeClient/RevertToSelf
// pair; WindowsIdentity.GetCurrent() inside the callback reflects the
// impersonated (connecting client's) token, not this elevated process's own.
SecurityIdentifier? authenticatedSid = null;
server.RunAsClient(() =>
{
    using var clientIdentity = WindowsIdentity.GetCurrent();
    authenticatedSid = clientIdentity.User;
});

if (authenticatedSid is null || !authenticatedSid.Equals(claimedSid))
{
    await WriteFailureResultAsync(server,
        "Authenticated client SID does not match the claimed SID. " +
        "Refusing to proceed.", cts.Token);
    return ExitAuthFailed;
}

if (!BootstrapProtocol.NoncesMatch(expectedNonce, hello.Nonce))
{
    await WriteFailureResultAsync(server, "Nonce mismatch. Refusing to proceed.", cts.Token);
    return ExitAuthFailed;
}

if (!string.Equals(hello.InstallerPath, installerPath, StringComparison.OrdinalIgnoreCase))
{
    await WriteFailureResultAsync(server,
        "Installer path in the authenticated hello does not match the launch argument. " +
        "Refusing to proceed.", cts.Token);
    return ExitAuthFailed;
}

// ── Verify the runtime descriptor (IR3/IKTD3) ───────────────────────────────
// The signed descriptor ships as an admin-protected sibling file next to
// this executable -- the same MSI-install/tamper protection the helper
// binary itself relies on (IR3/IKTD1). Never a value read from the
// user-writable installerPath/nonce/claimed-sid the app supplied.
RuntimeDescriptor descriptor;
try
{
    var descriptorPath = Path.Combine(AppContext.BaseDirectory, SignedRuntimeDescriptorFile.FileName);
    var descriptorJson = File.ReadAllText(descriptorPath);
    var descriptorFile = JsonSerializer.Deserialize(
        descriptorJson, SignedRuntimeDescriptorFileJsonContext.Default.SignedRuntimeDescriptorFile)
        ?? throw new InvalidOperationException("Descriptor file deserialized to null.");
    descriptor = DescriptorVerifier.VerifyOrThrow(descriptorFile.ToSignedRuntimeDescriptor());
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or FormatException)
{
    await WriteFailureResultAsync(server,
        $"Could not load or verify the signed runtime descriptor: {ex.Message}", cts.Token);
    return ExitDescriptorInvalid;
}

// ── Copy to an admin-only intermediate, then re-verify at THAT path (IR3) ──
// TOCTOU protection: re-hashing installerPath itself would still trust
// whatever bytes happen to be there at re-verify time, which a non-admin
// process could swap during the UAC-consent delay. Copying first to a path
// only this elevated process (and Administrators/SYSTEM) can write, then
// hashing the COPY, closes that window: nothing but this process could have
// written the bytes being verified.
var adminIntermediateDir = StagingHardening.CreateAdminOnlyDirectory();
string? adminIntermediatePath = null;
string? stagingDir = null;
var succeeded = false;
try
{
    adminIntermediatePath = Path.Combine(adminIntermediateDir, Path.GetFileName(installerPath));
    File.Copy(installerPath, adminIntermediatePath);

    var actualSha256 = ComputeSha256(adminIntermediatePath);
    if (!string.Equals(actualSha256, descriptor.InstallerSha256, StringComparison.OrdinalIgnoreCase))
    {
        await WriteFailureResultAsync(server,
            $"Installer SHA-256 mismatch at the protected re-verification path. " +
            $"Expected {descriptor.InstallerSha256}, got {actualSha256}. Refusing to launch.", cts.Token);
        return ExitShaMismatch;
    }

    // ── Stage entirely inside the admin-only ProgramData tree (IR3) ────────
    // The staging output is NEVER created under the requesting user's own
    // profile: that path is user-writable at every ancestor level, and no
    // path-string-based creation (however carefully validated beforehand)
    // can be made safe against the user replacing an ancestor with a
    // junction between validation and creation -- a real TOCTOU
    // privilege-escalation primitive, not a theoretical one. Instead, the
    // entire elevated sequence (copy, verify, vendor install, reparse scan,
    // ownership grant) happens inside a fresh, atomically-created,
    // admin/SYSTEM-only directory directly under %ProgramData% (itself a
    // stable, OS-protected anchor no non-admin can delete or replace). The
    // existing NON-ELEVATED resolver
    // (ManagedRuntimeResolver.CommitValidatedBundle) already moves whatever
    // staging path this reports into the user's own runtime directory as an
    // unelevated operation -- a non-elevated process following a
    // user-controlled junction can only ever act with the user's own
    // permissions, so no privilege escalation is possible even if the
    // user's own tree is later booby-trapped at that final, unelevated step.
    stagingDir = StagingHardening.CreateAdminOnlyDirectory();
    var psi = new ProcessStartInfo(adminIntermediatePath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        // NSIS requires /D to be the final argument and UNQUOTED even if the
        // path contains spaces -- ArgumentList auto-quotes on spaces, so the
        // raw Arguments string is built by hand instead.
        Arguments = $"/S /D={stagingDir}",
    };
    using var vendorProcess = Process.Start(psi)
        ?? throw new InvalidOperationException("Vendor installer process failed to start.");
    await vendorProcess.WaitForExitAsync(cts.Token);

    if (vendorProcess.ExitCode != 0)
    {
        await WriteFailureResultAsync(server,
            $"Vendor installer exited with code {vendorProcess.ExitCode}.", cts.Token);
        return ExitVendorLaunchFailed;
    }

    if (!Directory.Exists(stagingDir))
    {
        await WriteFailureResultAsync(server,
            "Vendor installer exited zero but produced no staging directory.", cts.Token);
        return ExitVendorLaunchFailed;
    }

    // ── Validate no reparse points, then ACL-harden the staging tree (IR3) ─
    if (StagingHardening.ContainsReparsePoint(stagingDir, out var reparsePointPath))
    {
        await WriteFailureResultAsync(server,
            $"Staging tree contains a reparse point at '{reparsePointPath}'; refusing to hand off " +
            "an installed tree that could redirect elsewhere once ownership transfers.",
            cts.Token);
        return ExitReparsePointDetected;
    }

    // ── Two-phase handoff (IR3): read-only staged result first ─────────────
    // Granting write access immediately would let anything running under
    // the requesting user's own SID (not just this legitimate resolver
    // call) mutate the tree while the caller is still validating it --
    // reopening the same TOCTOU class this whole design exists to close,
    // just moved into the unelevated validation window. Ownership/write
    // access transfers only after the caller reports a successful
    // validation over this SAME authenticated connection.
    StagingHardening.GrantReadOnlyAccess(stagingDir, claimedSid);

    var stagedResult = new BootstrapWireResult(ExitCode: 0, StagingDir: stagingDir, ErrorMessage: null);
    await WriteResultAsync(server, stagedResult, cts.Token);

    // The caller's own validation (ValidateContents/-version/TLS probe)
    // spawns real subprocesses and can take longer than the connection's
    // original budget -- a separate, more generous timeout governs waiting
    // for the finalize message specifically.
    using var finalizeCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    string finalizeLine;
    try
    {
        finalizeLine = await ReadLineAsync(server, finalizeCts.Token);
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException)
    {
        return ExitVendorLaunchFailed; // no one left to write a result to; staging cleaned up below
    }

    BootstrapFinalizeRequest finalize;
    try
    {
        finalize = JsonSerializer.Deserialize(finalizeLine, BootstrapWireJsonContext.Default.BootstrapFinalizeRequest)
            ?? throw new InvalidDataException("Empty finalize message.");
    }
    catch (JsonException)
    {
        await WriteFailureResultAsync(server, "Malformed finalize message. Refusing to proceed.", cts.Token);
        return ExitAuthFailed;
    }

    if (!BootstrapProtocol.NoncesMatch(expectedNonce, finalize.Nonce))
    {
        await WriteFailureResultAsync(server,
            "Finalize message nonce does not match the authenticated session. Refusing to proceed.", cts.Token);
        return ExitAuthFailed;
    }

    if (!finalize.Approve)
    {
        await WriteFailureResultAsync(server,
            "Caller reported validation failure; staging tree discarded without granting write access.", cts.Token);
        return ExitVendorLaunchFailed;
    }

    // Only now, after an authenticated approval, does ownership transfer.
    StagingHardening.HardenOwnership(stagingDir, claimedSid);

    succeeded = true;
    var finalResult = new BootstrapWireResult(ExitCode: 0, StagingDir: stagingDir, ErrorMessage: null);
    await WriteResultAsync(server, finalResult, cts.Token);
    return 0;
}
finally
{
    // The admin-only intermediate is a throwaway copy the vendor installer
    // reads from during launch -- cleaned up here regardless of which exit
    // path was taken. The staging directory is cleaned up here too UNLESS
    // finalization succeeded, where it is the deliberate handoff to the
    // non-elevated resolver and must survive this process's exit.
    if (adminIntermediatePath is not null)
    {
        try { File.Delete(adminIntermediatePath); } catch (IOException) { }
    }
    try { Directory.Delete(adminIntermediateDir); } catch (IOException) { }
    if (!succeeded && stagingDir is not null && Directory.Exists(stagingDir))
    {
        try { Directory.Delete(stagingDir, recursive: true); } catch (IOException) { }
    }
}


// ── Helpers ──────────────────────────────────────────────────────────────

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
        result[args[i][2..]] = args[i + 1];
        i++;
    }
    return result;
}

static string ComputeSha256(string filePath)
{
    using var sha = SHA256.Create();
    using var stream = File.OpenRead(filePath);
    return Convert.ToHexString(sha.ComputeHash(stream));
}

static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
{
    var buffer = new List<byte>();
    var single = new byte[1];
    while (true)
    {
        var read = await stream.ReadAsync(single, ct);
        if (read == 0)
            throw new EndOfStreamException("Client closed the pipe before sending a message.");
        if (single[0] == (byte)'\n') break;
        buffer.Add(single[0]);
    }
    return Encoding.UTF8.GetString(buffer.ToArray());
}

static Task WriteFailureResultAsync(Stream stream, string errorMessage, CancellationToken ct) =>
    WriteResultAsync(stream, new BootstrapWireResult(ExitCode: 1, StagingDir: null, ErrorMessage: errorMessage), ct);

static async Task WriteResultAsync(Stream stream, BootstrapWireResult result, CancellationToken ct)
{
    var line = JsonSerializer.Serialize(result, BootstrapWireJsonContext.Default.BootstrapWireResult);
    var bytes = Encoding.UTF8.GetBytes(line + "\n");
    await stream.WriteAsync(bytes, ct);
    await stream.FlushAsync(ct);
}

/// <summary>
/// Direct Win32 <c>CreateNamedPipeW</c> P/Invoke building a pipe whose DACL
/// grants connect/read/write ONLY to a single specified SID -- every other
/// principal is denied by omission. Used instead of the higher-level
/// <c>System.IO.Pipes.AccessControl</c> package (see the csproj comment for
/// why), matching this repo's existing convention of direct P/Invoke for
/// Win32-security-sensitive code (<c>HelperSignatureVerifier</c>'s
/// <c>WinVerifyTrust</c>).
/// </summary>
internal static class NativePipe
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;

    // Standard Win32 FILE_GENERIC_READ | FILE_GENERIC_WRITE access masks --
    // enough for a duplex byte-mode pipe connection, nothing more (no
    // FILE_GENERIC_EXECUTE, no WRITE_DAC/WRITE_OWNER/DELETE).
    private const int FileGenericRead = 0x00120089;
    private const int FileGenericWrite = 0x00120116;

    public static SafePipeHandle CreateDaclRestricted(string pipeName, SecurityIdentifier allowedSid)
    {
        var dacl = new DiscretionaryAcl(isContainer: false, isDS: false, capacity: 1);
        dacl.AddAccess(
            AccessControlType.Allow,
            allowedSid,
            FileGenericRead | FileGenericWrite,
            InheritanceFlags.None,
            PropagationFlags.None);

        var csd = new CommonSecurityDescriptor(
            isContainer: false, isDS: false, ControlFlags.None,
            owner: null, group: null, systemAcl: null, discretionaryAcl: dacl);

        var sdBytes = new byte[csd.BinaryLength];
        csd.GetBinaryForm(sdBytes, 0);

        var sdHandle = GCHandle.Alloc(sdBytes, GCHandleType.Pinned);
        try
        {
            var sa = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = sdHandle.AddrOfPinnedObject(),
                InheritHandle = 0,
            };

            var handle = CreateNamedPipeW(
                $@"\\.\pipe\{pipeName}",
                PipeAccessDuplex | FileFlagOverlapped,
                PipeTypeByte | PipeReadModeByte | PipeWait,
                nMaxInstances: 1,
                nOutBufferSize: 0,
                nInBufferSize: 0,
                nDefaultTimeOut: 0,
                ref sa);

            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateNamedPipeW failed.");

            return handle;
        }
        finally
        {
            sdHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipeW(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        ref SecurityAttributes lpSecurityAttributes);
}

/// <summary>
/// ACL/reparse-point hardening for the elevated install's admin-only
/// intermediate directory and the final staging tree (IR3/IU1 step 4).
/// </summary>
internal static class StagingHardening
{
    /// <summary>
    /// Create a fresh directory directly under <c>%ProgramData%</c> whose DACL
    /// grants access ONLY to Administrators and SYSTEM -- explicit,
    /// non-inherited. Deliberately ONE level directly under %ProgramData%
    /// itself (never <c>%ProgramData%\Serpy\...</c> multi-level nesting):
    /// %ProgramData% is a stable, OS-protected anchor no non-admin can
    /// delete or replace, but any *subfolder* under it (e.g. a "Serpy"
    /// folder) would itself need to already exist with a hardened ACL
    /// before it could be trusted the same way -- and creating THAT
    /// non-atomically at runtime would just move the TOCTOU race up one
    /// level instead of closing it. Single-level creation directly under
    /// the anchor is what makes <see cref="CreateLockedDirectory"/>'s
    /// atomic-with-baked-in-DACL creation actually sufficient.
    /// </summary>
    public static string CreateAdminOnlyDirectory()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            $"Serpy-Bootstrap-{Guid.NewGuid():N}");
        CreateLockedDirectory(root);
        return root;
    }

    /// <summary>
    /// Atomically create <paramref name="path"/> -- which must not already
    /// exist and whose PARENT must already exist as a stable, trusted anchor
    /// (this never creates missing ancestors) -- with its admin/SYSTEM-only
    /// DACL baked into the <c>CreateDirectoryW</c> call itself, not applied
    /// afterward via a separate <c>SetAccessControl</c>. Creating first with
    /// a default/inherited (often user-writable) ACL and locking it down as
    /// a second step would leave a privileged race window between those two
    /// operations; passing the security descriptor directly means the
    /// object is never observable in an unprotected state.
    /// <c>CreateDirectoryW</c> reports <c>ERROR_ALREADY_EXISTS</c> when it
    /// loses a race against another creator, which this treats as fatal
    /// rather than swallowing (unlike <see cref="Directory.CreateDirectory(string)"/>,
    /// which is silently idempotent for an existing directory).
    /// </summary>
    public static void CreateLockedDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException(
                $"Refusing to create '{path}': its parent '{parent}' does not already exist as a " +
                "trusted anchor. This method never creates missing ancestors -- doing so " +
                "non-atomically would only move the TOCTOU race up one path level.");

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var dacl = new DiscretionaryAcl(isContainer: true, isDS: false, capacity: 2);
        dacl.AddAccess(AccessControlType.Allow, adminsSid, unchecked((int)FileSystemRights.FullControl),
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);
        dacl.AddAccess(AccessControlType.Allow, systemSid, unchecked((int)FileSystemRights.FullControl),
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);
        var csd = new CommonSecurityDescriptor(
            isContainer: true, isDS: false, ControlFlags.None,
            owner: adminsSid, group: null, systemAcl: null, discretionaryAcl: dacl);
        var sdBytes = new byte[csd.BinaryLength];
        csd.GetBinaryForm(sdBytes, 0);

        var sdHandle = GCHandle.Alloc(sdBytes, GCHandleType.Pinned);
        try
        {
            var sa = new DirSecurityAttributes
            {
                Length = Marshal.SizeOf<DirSecurityAttributes>(),
                SecurityDescriptor = sdHandle.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            if (!CreateDirectoryW(path, ref sa))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorAlreadyExists)
                    throw new IOException(
                        $"Refusing to use staging path '{path}': it already exists. " +
                        "This must never happen for a freshly generated path and indicates either a " +
                        "GUID collision or a TOCTOU race by another process.");
                throw new Win32Exception(error, $"CreateDirectoryW failed for '{path}'.");
            }
        }
        finally
        {
            sdHandle.Free();
        }
    }

    private const int ErrorAlreadyExists = 183;

    [StructLayout(LayoutKind.Sequential)]
    private struct DirSecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateDirectoryW(string lpPathName, ref DirSecurityAttributes lpSecurityAttributes);

    /// <summary>Recursively check every file/directory under <paramref name="root"/> for a reparse point.</summary>
    public static bool ContainsReparsePoint(string root, out string? reparsePointPath)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            var attrs = File.GetAttributes(current);
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                reparsePointPath = current;
                return true;
            }
            if (attrs.HasFlag(FileAttributes.Directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    stack.Push(entry);
            }
        }
        reparsePointPath = null;
        return false;
    }

    /// <summary>
    /// Grant <paramref name="userSid"/> read+execute (never write) on the
    /// staging tree, WITHOUT changing ownership away from Administrators
    /// (IR3): this is the pre-finalize handoff state -- the staging tree
    /// must be inspectable by the requesting user's own validation call
    /// (<c>ValidateContents</c>/<c>-version</c>/TLS probe) without being
    /// mutable by anything else running under that same SID while
    /// validation is in progress. Full ownership/write access is granted
    /// only later, by <see cref="HardenOwnership"/>, after the caller
    /// reports a successful validation over the authenticated pipe.
    /// </summary>
    public static void GrantReadOnlyAccess(string stagingRoot, SecurityIdentifier userSid)
    {
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        ApplyReadOnlyAcl(stagingRoot, isDirectory: true, userSid, adminsSid, systemSid);
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingRoot, "*", SearchOption.AllDirectories))
        {
            var isDirectory = File.GetAttributes(entry).HasFlag(FileAttributes.Directory);
            ApplyReadOnlyAcl(entry, isDirectory, userSid, adminsSid, systemSid);
        }
    }

    private static void ApplyReadOnlyAcl(
        string path, bool isDirectory,
        SecurityIdentifier userSid, SecurityIdentifier adminsSid, SecurityIdentifier systemSid)
    {
        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetOwner(adminsSid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                userSid, FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                adminsSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetOwner(adminsSid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                userSid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                adminsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }

    /// <summary>
    /// Set owner = <paramref name="ownerSid"/> with full control (inheritable)
    /// on the staging root, preserving SYSTEM/Administrators, then explicitly
    /// re-applies the same grant to every existing descendant -- new
    /// inheritable ACEs added to a directory do not retroactively apply to
    /// files already created inside it (only to items created afterward), so
    /// hardening after the vendor installer has already populated the tree
    /// requires an explicit recursive walk, not reliance on inheritance alone.
    /// Never touches any path outside the staging tree.
    /// </summary>
    public static void HardenOwnership(string stagingRoot, SecurityIdentifier ownerSid)
    {
        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        ApplyOwnerAcl(stagingRoot, isDirectory: true, ownerSid, adminsSid, systemSid);

        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingRoot, "*", SearchOption.AllDirectories))
        {
            var isDirectory = File.GetAttributes(entry).HasFlag(FileAttributes.Directory);
            ApplyOwnerAcl(entry, isDirectory, ownerSid, adminsSid, systemSid);
        }
    }

    private static void ApplyOwnerAcl(
        string path, bool isDirectory,
        SecurityIdentifier ownerSid, SecurityIdentifier adminsSid, SecurityIdentifier systemSid)
    {
        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetOwner(ownerSid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                ownerSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                adminsSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetOwner(ownerSid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                ownerSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                adminsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }
}
