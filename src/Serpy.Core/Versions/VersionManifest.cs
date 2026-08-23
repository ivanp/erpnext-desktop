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
            /// Immutable Windows QEMU archive URL. The resolver downloads and
            /// SHA-256 verifies this archive before contained extraction.
            /// </summary>
            public string ArchiveUrl { get; set; } = string.Empty;

            /// <summary>SHA-256 of the immutable QEMU archive.</summary>
            public string ArchiveSha256 { get; set; } = string.Empty;

            public string SourceUrl { get; set; } = string.Empty;
            public string LicenseNoticeUrl { get; set; } = string.Empty;
        }
    }

    public sealed class DebianCloudImageSection
    {
        public string Version { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        /// <summary>SHA-512 published by Debian's official cloud image descriptor.</summary>
        public string Sha512 { get; set; } = string.Empty;
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
        public string Lock { get; set; } = string.Empty;
        public string MinVersion { get; set; } = string.Empty;
    }
}
