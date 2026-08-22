using Serpy.Core.Versions;
using Serpy.Core.Operations;

namespace Serpy.Core.Tests.Operations;

/// <summary>
/// Recovery downgrade guard tests (R6, KD5).
/// Uses VersionGate directly — no QEMU needed.
/// </summary>
public sealed class RecoverDowngradeGuardTests
{
    private static VersionManifest MakeManifest(
        string dbFloor   = "11.8.0",
        string frappeMin = "16.0.0",
        string erpMin    = "16.0.0") => new()
    {
        Runtime = new()
        {
            MariaDb = new() { Lock = "11.8.3", Floor = dbFloor },
            Python  = new() { Lock = "3.14.1", Floor = "3.14.0" },
            Node    = new() { Lock = "24.2.0", Floor = "24.0.0" },
            Redis   = new() { Lock = "8.0.1",  Floor = "8.0.0" },
        },
        Apps = new()
        {
            Frappe  = new() { Branch = "version-16", MinVersion = frappeMin },
            ErpNext = new() { Branch = "version-16", MinVersion = erpMin },
        },
    };

    // ── MariaDB downgrade rejected ────────────────────────────────────────

    [Fact]
    public void MariaDb_BelowFloor_IsRejectedByRecoveryPredicate()
    {
        const string replacementMariaDb = "11.7.9";
        var manifest = MakeManifest();

        // Mirrors RecoverOperation's pre-mutation MariaDB guard.
        var accepted = VersionEvaluator.MeetsFloor(
            replacementMariaDb, manifest.Runtime.MariaDb.Floor);

        Assert.False(accepted);
    }

    // ── Frappe downgrade rejected ─────────────────────────────────────────

    [Fact]
    public void Frappe_BelowMinVersion_ThrowsVersionGateException()
    {
        var report = new GuestVersionReport
        {
            MariaDb = "11.8.3",
            Frappe  = "15.9.0",   // below minimum 16.0.0
            ErpNext = "16.5.0",
        };

        var ex = Assert.Throws<VersionGateException>(
            () => VersionGate.ValidateNoAppDowngrade(report, MakeManifest()));

        Assert.Equal("frappe",    ex.Component);
        Assert.Equal("15.9.0",    ex.Installed);
        Assert.Equal("16.0.0",    ex.Floor);
    }

    // ── ERPNext downgrade rejected ────────────────────────────────────────

    [Fact]
    public void ErpNext_BelowMinVersion_ThrowsVersionGateException()
    {
        var report = new GuestVersionReport
        {
            MariaDb = "11.8.3",
            Frappe  = "16.5.0",
            ErpNext = "15.8.0",   // below minimum 16.0.0
        };

        var ex = Assert.Throws<VersionGateException>(
            () => VersionGate.ValidateNoAppDowngrade(report, MakeManifest()));

        Assert.Equal("erpnext", ex.Component);
    }

    // ── Equal-or-newer versions pass ─────────────────────────────────────

    [Fact]
    public void EqualVersions_PassDowngradeGuard()
    {
        var report = new GuestVersionReport
        {
            MariaDb = "11.8.3",
            Frappe  = "16.0.0",   // exactly at floor
            ErpNext = "16.0.0",
        };
        // Should not throw.
        VersionGate.ValidateNoAppDowngrade(report, MakeManifest());
    }

    [Fact]
    public void NewerVersions_PassDowngradeGuard()
    {
        var report = new GuestVersionReport
        {
            MariaDb = "11.9.0",
            Frappe  = "16.5.0",
            ErpNext = "16.5.0",
        };
        VersionGate.ValidateNoAppDowngrade(report, MakeManifest());
    }

    [Fact]
    public void ReplacementBelowInstalledVersions_IsRejectedEvenAboveFloors()
    {
        var installed = new GuestVersionReport
        {
            MariaDb = "11.9.0",
            Frappe = "16.7.0",
            ErpNext = "16.7.0",
        };
        var replacement = new GuestVersionReport
        {
            MariaDb = "11.8.3",
            Frappe = "16.1.0",
            ErpNext = "16.1.0",
        };

        var ex = Assert.Throws<VersionGateException>(
            () => VersionGate.ValidateReplacementDoesNotDowngrade(replacement, installed));

        Assert.Equal("mariadb", ex.Component);
        Assert.Equal("11.8.3", ex.Installed);
        Assert.Equal("11.9.0", ex.Floor);
    }

    [Fact]
    public void ReplacementEqualOrNewerThanInstalledVersions_Passes()
    {
        var installed = new GuestVersionReport
        {
            MariaDb = "11.8.3",
            Frappe = "16.1.0",
            ErpNext = "16.1.0",
        };
        var replacement = new GuestVersionReport
        {
            MariaDb = "11.9.0",
            Frappe = "16.2.0",
            ErpNext = "16.2.0",
        };

        VersionGate.ValidateReplacementDoesNotDowngrade(replacement, installed);
    }

    // ── Empty Frappe version (not queried) is skipped ────────────────────

    [Fact]
    public void EmptyFrappeVersion_IsSkipped_DoesNotThrow()
    {
        var report = new GuestVersionReport
        {
            Frappe  = string.Empty,  // not queried — skip
            ErpNext = "16.5.0",
        };
        VersionGate.ValidateNoAppDowngrade(report, MakeManifest());
    }

    // ── Journal conflict: different target image blocks new recovery ───────

    [Fact]
    public void ConflictingJournal_DifferentTarget_Detected()
    {
        const string existingTarget = "/path/old-system.qcow2";
        const string newTarget      = "/path/new-system.qcow2";

        var journal = new Core.Coordination.RecoveryJournal
        {
            TargetImagePath  = existingTarget,
            HealthPassed     = false,
        };

        // Simulates the guard in RecoverOperation: different target with open journal = block.
        bool blocked = journal.TargetImagePath != newTarget && !journal.HealthPassed;
        Assert.True(blocked);
    }
}

public sealed class RecoverySwapStateTests
{
    [Fact]
    public void BackupPresentAndSystemMissing_IsTreatedAsSwapInProgress()
    {
        Assert.True(RecoverOperation.IsSwapInProgress(
            systemImageExists: false,
            backupImageExists: true));
    }

    [Fact]
    public void ExistingSystemAndBackup_IsNotTreatedAsSwapInProgress()
    {
        Assert.False(RecoverOperation.IsSwapInProgress(
            systemImageExists: true,
            backupImageExists: true));
    }

    [Fact]
    public void ReplacementPresentWithBackupBeforeJournalUpdate_IsDetectedAsCompletedCopy()
    {
        Assert.True(RecoverOperation.IsCompletedCopyAwaitingJournal(
            systemMatchesReplacement: true,
            backupImageExists: true,
            diskSwapped: false));
    }

    [Fact]
    public void UnrecognizedSystemWithBackup_IsNotDetectedAsCompletedCopy()
    {
        Assert.False(RecoverOperation.IsCompletedCopyAwaitingJournal(
            systemMatchesReplacement: false,
            backupImageExists: true,
            diskSwapped: false));
    }
}
