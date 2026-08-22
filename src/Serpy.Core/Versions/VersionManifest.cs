namespace Serpy.Core.Versions;

public sealed class VersionManifest
{
    public QemuSection Qemu { get; set; } = new();
    public DebianCloudImageSection DebianCloudImage { get; set; } = new();
    public RuntimeSection Runtime { get; set; } = new();
    public AppsSection Apps { get; set; } = new();

    public sealed class QemuSection
    {
        public string Version { get; set; } = string.Empty;
        public WindowsBundle Windows { get; set; } = new();

        public sealed class WindowsBundle
        {
            /// <summary>
            /// Stefan Weil pre-built NSIS installer URL.
            /// ManagedRuntimeResolver downloads, SHA-verifies, then
            /// silently installs with elevation (/S /D=targetDir).
            /// Preferred over archiveUrl when set.
            /// </summary>
            public string InstallerUrl { get; set; } = string.Empty;

            /// <summary>SHA-256 of the NSIS installer file.</summary>
            public string Sha256 { get; set; } = string.Empty;

            /// <summary>
            /// CI-built zip URL (build-windows.ps1 output). Used when
            /// InstallerUrl is empty. Extracted without elevation.
            /// </summary>
            public string ArchiveUrl { get; set; } = string.Empty;

            /// <summary>SHA-256 of the CI-built zip (when archiveUrl is used).</summary>
            public string ArchiveSha256 { get; set; } = string.Empty;

            public string SourceUrl { get; set; } = string.Empty;
            public string LicenseNoticeUrl { get; set; } = string.Empty;
        }
    }

    public sealed class DebianCloudImageSection
    {
        public string Version { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string Release { get; set; } = string.Empty;
        public string Arch { get; set; } = string.Empty;
    }

    public sealed class RuntimeSection
    {
        public LockFloor Python { get; set; } = new();
        public LockFloor Node { get; set; } = new();
        public LockFloor MariaDb { get; set; } = new();
        public LockFloor Redis { get; set; } = new();
    }

    public sealed class LockFloor
    {
        public string Lock { get; set; } = string.Empty;
        public string Floor { get; set; } = string.Empty;
    }

    public sealed class AppsSection
    {
        public AppEntry Frappe { get; set; } = new();
        public AppEntry ErpNext { get; set; } = new();
    }

    public sealed class AppEntry
    {
        public string Branch { get; set; } = string.Empty;
        public string MinVersion { get; set; } = string.Empty;
    }
}
