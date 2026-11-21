using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Health;
using Serpy.Core.Operations;
using Serpy.Core.Setup;

namespace Serpy.Core.Tests.Setup;

public sealed class CompanyDataStepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"SerpyCompanyDataTest-{Guid.NewGuid():N}");

    public CompanyDataStepTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private StateStore NewStateStore(Action<ApplianceState>? init = null)
    {
        var store = new StateStore(Path.Combine(_dir, "state.json"));
        if (init is not null) store.Mutate(init);
        return store;
    }

    private SetupStateStore NewSetupStore(Action<SetupState>? init = null)
    {
        var store = new SetupStateStore(Path.Combine(_dir, "setup-state.json"));
        if (init is not null) store.Mutate(init);
        return store;
    }

    private HealthCredentials NewCredentials(string? password = null)
    {
        var creds = new HealthCredentials(Path.Combine(_dir, ".health-cred"));
        if (password is not null) creds.Store("site1.local", password);
        return creds;
    }

    private static OperationResult Success(Guid id, OperationKind kind, string message) =>
        new(id, kind, OperationOutcome.Success, message);

    private static OperationResult Fail(Guid id, OperationKind kind, string message, FailureCode code) =>
        new(id, kind, OperationOutcome.Failure, message, Code: code);

    private SetupContext MakeContext(bool journalOpen = false) => new()
    {
        RunId = Guid.NewGuid(),
        JournalOpen = journalOpen,
        Activity = _ => { },
        StepPercent = _ => { },
        Log = _ => { },
    };

    // ── Routing Table: Row 1 (absent data.img, not Initialized) ───────────────

    [Fact]
    public async Task Row1_AbsentData_NotInitialized_DeletesLeftovers_AndCreatesNewData()
    {
        var stateStore = NewStateStore(s => s.Readiness = ReadinessState.NotBuilt);
        var setupStore = NewSetupStore();
        var pendingPath = Path.Combine(_dir, ".pending-data.img");
        File.WriteAllText(pendingPath, "leftover pending data");

        string? createdSite = null;
        string? createdPassword = null;

        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            applianceDir: () => _dir,
            initializeWork: (site, pwd, _, _) =>
            {
                createdSite = site;
                createdPassword = pwd;
                return Task.FromResult(Success(Guid.NewGuid(), OperationKind.Initialize, "ok"));
            });

        var context = MakeContext();
        step.DeleteLeftovers(context);
        Assert.False(File.Exists(pendingPath), "Leftover .pending-data.img must be deleted");

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal("site1.local", createdSite);
        Assert.Equal("admin", createdPassword);
        Assert.Equal(CompanyDataRoute.CreateNew, setupStore.Read().CompanyDataRoute);
    }

    // ── Routing Table: Row 2 (present data.img, Initialized) ───────────────────

    [Fact]
    public void Row2_PresentData_Initialized_IsDoneReturnsTrue_StepSkipped()
    {
        var stateStore = NewStateStore(s => s.Readiness = ReadinessState.Initialized);
        var setupStore = NewSetupStore();
        File.WriteAllText(Path.Combine(_dir, "data.img"), "committed data");

        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            applianceDir: () => _dir);

        var context = MakeContext();
        var preflight = step.Preflight(context);

        Assert.Null(preflight);
        Assert.True(step.IsDone(context));
    }

    // ── Routing Table: Row 3 (present data.img, not Initialized) ───────────────

    [Fact]
    public async Task Row3_PresentData_NotInitialized_DecidesAdoptRoute_AndAdoptsInPlace()
    {
        var stateStore = NewStateStore(s => s.Readiness = ReadinessState.NotBuilt);
        var setupStore = NewSetupStore();
        var creds = NewCredentials("savedSecret");
        var dataFile = Path.Combine(_dir, "data.img");
        File.WriteAllText(dataFile, "existing company data");

        bool adoptExecuted = false;
        string? passedPassword = null;
        bool legacyConsent = false;

        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            healthCredentials: creds,
            applianceDir: () => _dir,
            adoptWork: (param, consent, _, _) =>
            {
                adoptExecuted = true;
                passedPassword = param.AdminPassword;
                legacyConsent = consent;
                return Task.FromResult(Success(Guid.NewGuid(), OperationKind.Adopt, "adopted"));
            });

        var context = MakeContext();
        var preflight = step.Preflight(context);
        Assert.Null(preflight);
        Assert.Equal(CompanyDataRoute.AdoptExisting, setupStore.Read().CompanyDataRoute);

        var result = await step.RunAsync(context, CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.True(adoptExecuted);
        Assert.Equal("savedSecret", passedPassword);
        Assert.True(legacyConsent, "KTD11: Consent for existing data without provenance is implied");
    }

    // ── Routing Table: Row 4 (absent data.img, Initialized) ───────────────────

    [Fact]
    public async Task Row4_AbsentData_Initialized_StopsWithDataUnusable_CreatesNothing()
    {
        var stateStore = NewStateStore(s => s.Readiness = ReadinessState.Initialized);
        var setupStore = NewSetupStore();

        bool initCalled = false;
        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            applianceDir: () => _dir,
            initializeWork: (_, _, _, _) =>
            {
                initCalled = true;
                return Task.FromResult(Success(Guid.NewGuid(), OperationKind.Initialize, "ok"));
            });

        var context = MakeContext();
        var preflight = step.Preflight(context);

        Assert.NotNull(preflight);
        Assert.Equal(OperationOutcome.Failure, preflight!.Outcome);
        Assert.Equal(FailureCode.DataUnusable, preflight.Code);

        var runResult = await step.RunAsync(context, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failure, runResult.Outcome);
        Assert.Equal(FailureCode.DataUnusable, runResult.Code);
        Assert.False(initCalled);
        Assert.False(File.Exists(Path.Combine(_dir, "data.img")));
    }

    // ── Routing Table: Row 5 (recovery journal that has not passed) ────────────

    [Fact]
    public async Task Row5_RecoveryJournalNotPassed_StopsWithDataUnusable_RecoverFlowOwnsIt()
    {
        var stateStore = NewStateStore(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.RecoveryJournal = new RecoveryJournal { HealthPassed = false };
        });
        var setupStore = NewSetupStore();
        File.WriteAllText(Path.Combine(_dir, "data.img"), "data");

        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            applianceDir: () => _dir);

        var context = MakeContext();
        var preflight = step.Preflight(context);

        Assert.NotNull(preflight);
        Assert.Equal(OperationOutcome.Failure, preflight!.Outcome);
        Assert.Equal(FailureCode.DataUnusable, preflight.Code);
        Assert.Contains("Recover flow owns it", preflight.Message);

        var runResult = await step.RunAsync(context, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failure, runResult.Outcome);
        Assert.Equal(FailureCode.DataUnusable, runResult.Code);
    }

    // ── Routing Table: Row 6 (adoption journal open) ──────────────────────────

    [Fact]
    public void Row6_AdoptionJournalOpen_ReconcilesJournal_ThenEvaluatesAgain()
    {
        var committedPath = Path.Combine(_dir, "data.img");
        var backupPath = committedPath + ".bak";
        File.WriteAllText(backupPath, "original restored dataset");
        File.WriteAllText(committedPath, "corrupt promoted dataset");

        var stateStore = NewStateStore(s =>
        {
            s.Readiness = ReadinessState.Initialized;
            s.AdoptionJournal = new AdoptionJournal
            {
                Stage = AdoptionStage.DataPromoted,
                CommittedDataPath = committedPath,
                PendingDataPath = Path.Combine(_dir, ".pending-adopted-data.img"),
            };
        });
        var creds = NewCredentials();
        var setupStore = NewSetupStore();

        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            healthCredentials: creds,
            applianceDir: () => _dir);

        var context = MakeContext();
        var preflight = step.Preflight(context);

        Assert.Null(preflight);
        Assert.Null(stateStore.Read().AdoptionJournal);
        Assert.Equal("original restored dataset", File.ReadAllText(committedPath));
    }

    // ── Saved Route Persistence across Build ──────────────────────────────────

    [Fact]
    public async Task SavedRoute_AdoptExisting_PersistsAcrossBuildPreservingData()
    {
        var stateStore = NewStateStore(s => s.Readiness = ReadinessState.NotBuilt);
        var setupStore = NewSetupStore();
        var dataFile = Path.Combine(_dir, "data.img");
        File.WriteAllText(dataFile, "data");

        bool adopted = false;
        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            healthCredentials: NewCredentials(),
            applianceDir: () => _dir,
            adoptWork: (_, _, _, _) =>
            {
                adopted = true;
                return Task.FromResult(Success(Guid.NewGuid(), OperationKind.Adopt, "ok"));
            });

        var context = MakeContext();
        step.Preflight(context);
        Assert.Equal(CompanyDataRoute.AdoptExisting, setupStore.Read().CompanyDataRoute);

        // Build now runs with data preserved and marks readiness Initialized
        stateStore.Mutate(s => s.Readiness = ReadinessState.Initialized);

        // CompanyDataStep is NOT considered done yet because the route decided before Build was AdoptExisting
        Assert.False(step.IsDone(context), "Step must not be skipped when route is AdoptExisting until adoption runs");

        var result = await step.RunAsync(context, CancellationToken.None);
        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.True(adopted);
    }

    // ── GetExtraDiskBytes ─────────────────────────────────────────────────────

    [Fact]
    public void GetExtraDiskBytes_ForAdoptExisting_ReturnsFileSizePlus2GiB()
    {
        var dataFile = Path.Combine(_dir, "data.img");
        var bytes = new byte[1024];
        File.WriteAllBytes(dataFile, bytes);

        var setupStore = NewSetupStore(s => s.CompanyDataRoute = CompanyDataRoute.AdoptExisting);
        var stateStore = NewStateStore();

        long extra = CompanyDataStep.GetExtraDiskBytes(_dir, setupStore, stateStore);
        Assert.Equal(1024 + 2L * 1024 * 1024 * 1024, extra);
    }

    [Fact]
    public void GetExtraDiskBytes_ForCreateNew_ReturnsZero()
    {
        var setupStore = NewSetupStore(s => s.CompanyDataRoute = CompanyDataRoute.CreateNew);
        var stateStore = NewStateStore();

        long extra = CompanyDataStep.GetExtraDiskBytes(_dir, setupStore, stateStore);
        Assert.Equal(0L, extra);
    }

    // ── Password Handling in Adoption ─────────────────────────────────────────

    [Fact]
    public async Task ExistingData_WithNoSavedCredential_TriesAdmin_AndSucceeds()
    {
        var stateStore = NewStateStore(s => s.Readiness = ReadinessState.NotBuilt);
        var setupStore = NewSetupStore();
        var emptyCreds = NewCredentials(); // no credential saved
        File.WriteAllText(Path.Combine(_dir, "data.img"), "data");

        string? usedPassword = null;
        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            healthCredentials: emptyCreds,
            applianceDir: () => _dir,
            adoptWork: (param, _, _, _) =>
            {
                usedPassword = param.AdminPassword;
                return Task.FromResult(Success(Guid.NewGuid(), OperationKind.Adopt, "ok"));
            });

        var result = await step.RunAsync(MakeContext(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Success, result.Outcome);
        Assert.Equal("admin", usedPassword);
    }

    [Fact]
    public async Task ExistingData_WithNeitherSavedNorAdminWorking_ReturnsPasswordRejected()
    {
        var stateStore = NewStateStore(s => s.Readiness = ReadinessState.NotBuilt);
        var setupStore = NewSetupStore();
        var emptyCreds = NewCredentials();
        File.WriteAllText(Path.Combine(_dir, "data.img"), "data");

        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            healthCredentials: emptyCreds,
            applianceDir: () => _dir,
            adoptWork: (_, _, _, _) =>
                Task.FromResult(Fail(Guid.NewGuid(), OperationKind.Adopt, "Incorrect password", FailureCode.PasswordRejected)));

        var result = await step.RunAsync(MakeContext(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.PasswordRejected, result.Code);
    }

    // ── Existing data never overwritten by create-new ─────────────────────────

    [Fact]
    public async Task CreateNewPath_NeverOverwritesExistingData()
    {
        var stateStore = NewStateStore(s => s.Readiness = ReadinessState.NotBuilt);
        var setupStore = NewSetupStore(s => s.CompanyDataRoute = CompanyDataRoute.CreateNew);
        File.WriteAllText(Path.Combine(_dir, "data.img"), "important company data");

        bool initCalled = false;
        var step = new CompanyDataStep(
            initialize: null!,
            stateStore: stateStore,
            setupStore: setupStore,
            applianceDir: () => _dir,
            initializeWork: (_, _, _, _) =>
            {
                initCalled = true;
                return Task.FromResult(Success(Guid.NewGuid(), OperationKind.Initialize, "ok"));
            });

        var result = await step.RunAsync(MakeContext(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Failure, result.Outcome);
        Assert.Equal(FailureCode.DataUnusable, result.Code);
        Assert.False(initCalled);
        Assert.Equal("important company data", File.ReadAllText(Path.Combine(_dir, "data.img")));
    }
}
