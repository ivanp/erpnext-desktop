using System.Runtime.Versioning;
using System.Runtime.CompilerServices;

// This whole assembly is a Windows-only elevated helper: named pipes with
// ACL/impersonation, Authenticode verification, and NSIS installer launch are
// all Win32-only concerns. Marked at assembly level rather than per-file.

[assembly: InternalsVisibleTo("Serpy.InstallerBootstrapper.Tests")]
[assembly: SupportedOSPlatform("windows")]
