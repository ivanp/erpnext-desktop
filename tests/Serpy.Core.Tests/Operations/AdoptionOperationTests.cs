using Serpy.Core.Health;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Images;
using Serpy.Core.Operations;
using Serpy.Core.Versions;

namespace Serpy.Core.Tests.Operations;

public sealed class AdoptionOperationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyAdoptTest-{Guid.NewGuid():N}");

    public AdoptionOperationTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static VersionManifest CreateManifest() => new()
    {
        Runtime = new VersionManifest.RuntimeSection
        {
            Python = new VersionManifest.LockFloor { Lock = "3.14.7", Floor = "3.14.0" },
            Node = new VersionManifest.LockFloor { Lock = "24.2.0", Floor = "24.0.0" },
            MariaDb = new VersionManifest.LockFloor { Lock = "11.8.3", Floor = "11.8.0" },
            Redis = new VersionManifest.LockFloor { Lock = "8.0.2", Floor = "8.0.0" },
        },
        Apps = new VersionManifest.AppsSection
        {
            Frappe = new VersionManifest.AppEntry { Branch = "v16.0.0", Commit = "6a329d068416768ec47ccd3326b9cc95a8d7bf99", Lock = "16.0.0", MinVersion = "16.0.0" },
            ErpNext = new VersionManifest.AppEntry { Branch = "v16.0.0", Commit = "11e0ba0a1c45f217e2e73e885f699102d06da325", Lock = "16.0.0", MinVersion = "16.0.0" },
        },
    };

    [Fact]
    public async Task InspectArchive_MissingFile_ReturnsRejected()
    {
        var op = new InspectArchiveOperation(CreateManifest());
        var inspection = await op.ExecuteAsync(Path.Combine(_dir, "nonexistent.img"));

        Assert.Equal(ArchiveInspectionVerdict.Rejected, inspection.Verdict);
        Assert.NotNull(inspection.RejectionReason);
    }

    [Fact]
    public async Task InspectArchive_UnlabeledLegacyData_ReturnsLegacyConsentRequired()
    {
        string archivePath = Path.Combine(_dir, "legacy-data.img");
        File.WriteAllBytes(archivePath, new byte[1024]);

        var op = new InspectArchiveOperation(CreateManifest());
        var inspection = await op.ExecuteAsync(archivePath);

        Assert.Equal(ArchiveInspectionVerdict.LegacyConsentRequired, inspection.Verdict);
        Assert.Null(inspection.ExpectedDigest);
    }

    [Fact]
    public async Task ProvenanceRecordStore_WriteAndRead_RoundTrips()
    {
        string archivePath = Path.Combine(_dir, "test-data.img");
        File.WriteAllBytes(archivePath, new byte[1024]);

        var record = new ProvenanceRecord
        {
            ArchiveSha256 = "ABCDEF1234567890",
            CreatedTimestamp = DateTimeOffset.UtcNow,
            GuestVersions = new GuestVersionReport
            {
                Python = "Python 3.14.0",
                Node = "v22.0.0",
                MariaDb = "MariaDB 11.4.0",
                Redis = "Redis 7.2.0",
                Frappe = "v16.0.0",
                ErpNext = "v16.0.0",
            }
        };

        ProvenanceRecordStore.Write(archivePath, record);

        var loaded = ProvenanceRecordStore.Read(archivePath);
        Assert.NotNull(loaded);
        Assert.Equal("ABCDEF1234567890", loaded.ArchiveSha256);
        Assert.Equal("v16.0.0", loaded.GuestVersions.Frappe);
    }

    [Fact]
    public async Task InspectArchive_AuthenticatedProvenance_ReturnsAuthenticatedPreflightPassed()
    {
        string archivePath = Path.Combine(_dir, "valid-data.img");
        File.WriteAllBytes(archivePath, new byte[1024]);

        var record = new ProvenanceRecord
        {
            ArchiveSha256 = "123456",
            CreatedTimestamp = DateTimeOffset.UtcNow,
            GuestVersions = new GuestVersionReport
            {
                Python = "Python 3.14.0",
                Node = "v22.0.0",
                MariaDb = "MariaDB 11.4.0",
                Redis = "Redis 7.2.0",
                Frappe = "v16.0.0",
                ErpNext = "v16.0.0",
            }
        };

        ProvenanceRecordStore.Write(archivePath, record);

        var op = new InspectArchiveOperation(CreateManifest());
        var inspection = await op.ExecuteAsync(archivePath);

        Assert.Equal(ArchiveInspectionVerdict.AuthenticatedPreflightPassed, inspection.Verdict);
        Assert.Equal("123456", inspection.ExpectedDigest);
    }

    [Fact]
    public void ReconcileJournal_ReadyToCommitStage_CleansPendingCopyAndJournal()
    {
        string statePath = Path.Combine(_dir, "state-ready.json");
        string pendingPath = Path.Combine(_dir, ".pending-data.img");
        File.WriteAllBytes(pendingPath, new byte[512]);

        var store = new StateStore(statePath);
        store.Mutate(s =>
        {
            s.AdoptionJournal = new AdoptionJournal
            {
                Stage = AdoptionStage.ReadyToCommit,
                PendingDataPath = pendingPath,
                CommittedDataPath = Path.Combine(_dir, "data.img"),
            };
        });

        var creds = new HealthCredentials(Path.Combine(_dir, ".cred"));
        AdoptOperation.ReconcileJournal(store, creds);

        Assert.False(File.Exists(pendingPath));
        Assert.Null(store.Read().AdoptionJournal);
    }

    [Fact]
    public void ReconcileJournal_DataPromotedStage_RollsBackToBackupDataAndClearsJournal()
    {
        string statePath = Path.Combine(_dir, "state-promoted.json");
        string committedPath = Path.Combine(_dir, "data.img");
        string backupPath = committedPath + ".bak";

        byte[] originalBackupBytes = [1, 2, 3, 4, 5];
        byte[] uncommittedPromotedBytes = [9, 9, 9, 9, 9];
        File.WriteAllBytes(backupPath, originalBackupBytes);
        File.WriteAllBytes(committedPath, uncommittedPromotedBytes);

        var store = new StateStore(statePath);
        store.Mutate(s =>
        {
            s.AdoptionJournal = new AdoptionJournal
            {
                Stage = AdoptionStage.DataPromoted,
                CommittedDataPath = committedPath,
            };
        });

        var creds = new HealthCredentials(Path.Combine(_dir, ".cred"));
        AdoptOperation.ReconcileJournal(store, creds);

        var state = store.Read();
        Assert.Null(state.AdoptionJournal);
        Assert.True(File.Exists(committedPath));
        Assert.Equal(originalBackupBytes, File.ReadAllBytes(committedPath));
    }

    [Fact]
    public void ReconcileJournal_CredentialStoredStage_CleansBackupAndClearsJournal()
    {
        string statePath = Path.Combine(_dir, "state-cred.json");
        string committedPath = Path.Combine(_dir, "data.img");
        string backupPath = committedPath + ".bak";
        File.WriteAllBytes(committedPath, new byte[512]);
        File.WriteAllBytes(backupPath, new byte[512]);

        var store = new StateStore(statePath);
        store.Mutate(s =>
        {
            s.AdoptionJournal = new AdoptionJournal
            {
                Stage = AdoptionStage.CredentialStored,
                CommittedDataPath = committedPath,
            };
        });

        var creds = new HealthCredentials(Path.Combine(_dir, ".cred"));
        AdoptOperation.ReconcileJournal(store, creds);

        Assert.False(File.Exists(backupPath));
        Assert.Null(store.Read().AdoptionJournal);
        Assert.Equal(ReadinessState.Initialized, store.Read().Readiness);
    }

    [Fact]
    public void ReconcileJournal_InterruptedAtStateCommitted_RestoresBackupDataAndBackupCredential()
    {
        string statePath = Path.Combine(_dir, "state-interrupted.json");
        string committedPath = Path.Combine(_dir, "data.img");
        string backupPath = committedPath + ".bak";

        byte[] originalBackupBytes = [1, 2, 3];
        byte[] uncommittedBytes = [9, 9, 9];
        File.WriteAllBytes(backupPath, originalBackupBytes);
        File.WriteAllBytes(committedPath, uncommittedBytes);

        string credPath = Path.Combine(_dir, ".health-cred");
        string credBackupPath = credPath + ".bak";
        var creds = new HealthCredentials(credPath);
        var backupCreds = new HealthCredentials(credBackupPath);

        backupCreds.Store("site1.local", "oldPassword");
        creds.Store("site1.local", "newPassword"); // Simulates Store succeeding before stage mutation

        var store = new StateStore(statePath);
        store.Mutate(s =>
        {
            s.AdoptionJournal = new AdoptionJournal
            {
                Stage = AdoptionStage.StateCommitted,
                CommittedDataPath = committedPath,
            };
        });

        AdoptOperation.ReconcileJournal(store, creds);

        var state = store.Read();
        Assert.Null(state.AdoptionJournal);
        Assert.Equal(originalBackupBytes, File.ReadAllBytes(committedPath));
        Assert.Equal("oldPassword", creds.Retrieve()?.AdminPassword);
    }

    [Fact]
    public async Task InsufficientDiskSpace_ReturnsDiskFull_BeforeAnyCopy_AndLeavesArchiveUntouched()
    {
        var applianceDir = Path.Combine(_dir, "appliance");
        Directory.CreateDirectory(applianceDir);
        var archivePath = Path.Combine(applianceDir, "data.img");
        var archiveBytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(archivePath, archiveBytes);

        var systemPath = Path.Combine(applianceDir, "system.qcow2");
        File.WriteAllText(systemPath, "system");

        // Force KnownPaths.ApplianceDir override if needed, or point SystemImagePath to appliance
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        var creds = new HealthCredentials(Path.Combine(_dir, "creds"));
        var settings = new ApplianceSettings();
        var manifest = CreateManifest();

        // Free space is 500 bytes, far less than file length + 2 GiB
        var op = new AdoptOperation(
            null!, null!, null!, creds, store, manifest, settings,
            freeSpaceBytes: () => 500);

        var inspection = new ArchiveInspection(
            ArchiveInspectionVerdict.AuthenticatedPreflightPassed,
            archivePath,
            "expected-digest");

        var parameters = new AdoptParameters(archivePath, "admin");
        var progress = new Progress<OperationUpdate>();

        var prevAppdata = Environment.GetEnvironmentVariable("SERPY_TEST_APPDATA");
        try
        {
            Environment.SetEnvironmentVariable("SERPY_TEST_APPDATA", applianceDir);
            var result = await op.ExecuteAsync(
                Guid.NewGuid(), parameters, inspection, legacyConsentApproved: true, progress, CancellationToken.None);

            Assert.Equal(OperationOutcome.Failure, result.Outcome);
            Assert.Equal(FailureCode.DiskFull, result.Code);
            Assert.Contains("Insufficient free disk space", result.Message);
            Assert.Equal(archiveBytes, File.ReadAllBytes(archivePath));
            Assert.False(File.Exists(Path.Combine(applianceDir, ".pending-adopted-data.img")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SERPY_TEST_APPDATA", prevAppdata);
        }
    }

    [Fact]
    public async Task RejectedInspection_ReturnsDataUnusable_AndLeavesDataUntouched()
    {
        var applianceDir = Path.Combine(_dir, "appliance-rej");
        Directory.CreateDirectory(applianceDir);
        var archivePath = Path.Combine(applianceDir, "data.img");
        var archiveBytes = new byte[] { 9, 8, 7, 6 };
        File.WriteAllBytes(archivePath, archiveBytes);

        var store = new StateStore(Path.Combine(_dir, "state-rej.json"));
        var creds = new HealthCredentials(Path.Combine(_dir, "creds-rej"));
        var settings = new ApplianceSettings();
        var manifest = CreateManifest();

        var op = new AdoptOperation(null!, null!, null!, creds, store, manifest, settings);

        var inspection = new ArchiveInspection(
            ArchiveInspectionVerdict.Rejected,
            archivePath,
            ExpectedDigest: null,
            RejectionReason: "Incompatible dataset version: downgrade rejected");

        var parameters = new AdoptParameters(archivePath, "admin");
        var progress = new Progress<OperationUpdate>();

        var result = await op.ExecuteAsync(
            Guid.NewGuid(), parameters, inspection, legacyConsentApproved: true, progress, CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.DataUnusable, result.Code);
        Assert.Contains("downgrade rejected", result.Message);
        Assert.Equal(archiveBytes, File.ReadAllBytes(archivePath));
    }
}
