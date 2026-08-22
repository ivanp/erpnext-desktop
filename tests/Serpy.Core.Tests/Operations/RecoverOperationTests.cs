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
    public void CurrentSystemImage_IsRejectedAsRecoveryReplacement()
    {
        Assert.True(RecoverOperation.IsCurrentSystemImageReplacement(
            "C:\\Users\\Test\\AppData\\Local\\Serpy\\appliance\\system.qcow2",
            "C:\\Users\\Test\\AppData\\Local\\Serpy\\appliance\\SYSTEM.qcow2"));
    }

    [Fact]
    public void DifferentImage_IsNotRejectedAsCurrentSystemImage()
    {
        Assert.False(RecoverOperation.IsCurrentSystemImageReplacement(
            "C:\\Users\\Test\\AppData\\Local\\Serpy\\appliance\\system.qcow2",
            "C:\\Users\\Test\\Downloads\\replacement.qcow2"));
    }

    [Fact]
    public void JunctionAliasToCurrentSystemImage_IsRejectedAsRecoveryReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SerpyRecoverAlias-{Guid.NewGuid():N}");
        var currentDirectory = Path.Combine(directory, "current");
        var aliasDirectory = Path.Combine(directory, "alias");
        Directory.CreateDirectory(currentDirectory);
        try
        {
            var systemImage = Path.Combine(currentDirectory, "system.qcow2");
            var replacement = Path.Combine(aliasDirectory, "system.qcow2");
            File.WriteAllText(systemImage, "accepted image");

            using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{aliasDirectory}\" \"{currentDirectory}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            junction!.WaitForExit();
            Assert.Equal(0, junction.ExitCode);

            Assert.True(RecoverOperation.IsCurrentSystemImageReplacement(systemImage, replacement));
        }
        finally
        {
            if (Directory.Exists(aliasDirectory))
                System.Diagnostics.Process.Start("cmd.exe", $"/c rmdir \"{aliasDirectory}\"")!.WaitForExit();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HardLinkAliasToCurrentSystemImage_IsRejectedAsRecoveryReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SerpyRecoverHardLink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var systemImage = Path.Combine(directory, "system.qcow2");
            var replacement = Path.Combine(directory, "replacement.qcow2");
            File.WriteAllText(systemImage, "accepted image");

            using var hardLink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /H \"{replacement}\" \"{systemImage}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            hardLink!.WaitForExit();
            Assert.Equal(0, hardLink.ExitCode);

            Assert.True(RecoverOperation.IsCurrentSystemImageReplacement(systemImage, replacement));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void JournalledSwapWithSystemMissing_BypassesInstalledImageValidation()
    {
        Assert.True(RecoverOperation.IsInterruptedSwapAwaitingCopy(
            journalTargetsReplacement: true,
            systemImageExists: false,
            backupImageExists: true,
            diskSwapped: false));
    }

    [Fact]
    public void UnjournalledMissingSystem_DoesNotBypassInstalledImageValidation()
    {
        Assert.False(RecoverOperation.IsInterruptedSwapAwaitingCopy(
            journalTargetsReplacement: false,
            systemImageExists: false,
            backupImageExists: true,
            diskSwapped: false));
    }

    [Fact]
    public void VerifiedReplacementWithDistinctSource_IsDetectedAsCompletedCopy()
    {
        Assert.True(RecoverOperation.IsCompletedCopyAwaitingJournal(
            currentMatchesReplacement: true,
            originalImageSha256: "original-digest",
            replacementImageSha256: "replacement-digest",
            backupImageExists: true,
            diskSwapped: false));
    }

    [Fact]
    public void DuplicateReplacementWithBackup_IsNotTreatedAsCompletedCopy()
    {
        Assert.False(RecoverOperation.IsCompletedCopyAwaitingJournal(
            currentMatchesReplacement: true,
            originalImageSha256: "same-digest",
            replacementImageSha256: "same-digest",
            backupImageExists: true,
            diskSwapped: false));
    }

    [Fact]
    public void UnverifiedCurrentImage_IsNotDetectedAsCompletedCopy()
    {
        Assert.False(RecoverOperation.IsCompletedCopyAwaitingJournal(
            currentMatchesReplacement: false,
            originalImageSha256: "original-digest",
            replacementImageSha256: "replacement-digest",
            backupImageExists: true,
            diskSwapped: false));
    }
}
