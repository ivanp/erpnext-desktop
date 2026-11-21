using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Health;
using Serpy.Core.Operations;

namespace Serpy.Core.Setup;

/// <summary>
/// Creates or adopts company data (KTD7, KTD10, KTD11, KTD12).
/// Follows the company data routing table:
///   - absent data.img and not Initialized: create new data
///   - present data.img and Initialized: skip the step
///   - present data.img and not Initialized: Build with data preserved, then adopt in place
///   - absent data.img and Initialized: stop with DataUnusable
///   - recovery journal that has not passed: stop with DataUnusable (Recover flow owns it)
///   - adoption journal: reconcile journal, then evaluate again
///
/// Guided setup creates the site <c>site1.local</c> with the password <c>admin</c>.
/// The route is decided once before Build and saved in <c>SetupState.CompanyDataRoute</c> (KTD11).
/// </summary>
public sealed class CompanyDataStep(
    InitializeOperation initialize,
    StateStore stateStore,
    AdoptOperation? adopt = null,
    InspectArchiveOperation? inspectArchive = null,
    SetupStateStore? setupStore = null,
    HealthCredentials? healthCredentials = null,
    Func<string>? applianceDir = null,
    Func<string, string, IProgress<OperationUpdate>, CancellationToken, Task<OperationResult>>? initializeWork = null,
    Func<AdoptParameters, bool, IProgress<OperationUpdate>, CancellationToken, Task<OperationResult>>? adoptWork = null) : ISetupStep
{
    public const string SiteName = "site1.local";

    // The password of new data. It is the documented default of KTD12 and is shown on the Ready
    // screen; the user changes it inside ERPNext.
    internal const string DefaultPassword = "admin";

    private readonly InitializeOperation _initialize = initialize;
    private readonly StateStore _stateStore = stateStore;
    private readonly AdoptOperation? _adopt = adopt;
    private readonly InspectArchiveOperation? _inspectArchive = inspectArchive;
    private readonly SetupStateStore? _setupStore = setupStore;
    private readonly HealthCredentials? _healthCredentials = healthCredentials;
    private readonly Func<string> _applianceDir = applianceDir ?? (() => KnownPaths.ApplianceDir);

    public SetupStepId Id => SetupStepId.CompanyData;
    public int Weight => 15;
    public TimeSpan QuietTime => TimeSpan.FromMinutes(10);

    private string Data => Path.Combine(_applianceDir(), "data.img");
    private string Pending => Path.Combine(_applianceDir(), ".pending-data.img");
    private string PendingAdopted => Path.Combine(_applianceDir(), ".pending-adopted-data.img");

    /// <summary>
    /// Decides whether the step is already satisfied. When the route was decided before Build as
    /// AdoptExisting, the step is only done when adoption has completed (KTD11).
    /// </summary>
    public bool IsDone(SetupContext context)
    {
        var route = _setupStore?.Read().CompanyDataRoute ?? CompanyDataRoute.Undecided;
        if (route == CompanyDataRoute.AdoptExisting)
        {
            return _setupStore?.Read().Steps.GetValueOrDefault(SetupStepId.CompanyData) == SetupStepState.Done;
        }

        return File.Exists(Data) && _stateStore.Read().Readiness == ReadinessState.Initialized;
    }

    /// <summary>
    /// Evaluates routing table preflight conditions before any step runs (KTD11).
    /// Saves the decided route in SetupState so Build can preserve data if needed.
    /// </summary>
    public SetupStepResult? Preflight(SetupContext context)
    {
        // Journal reconciliation: if an adoption journal is open, reconcile it first (KTD10, KTD11)
        if (_stateStore.Read().AdoptionJournal is not null)
        {
            AdoptOperation.ReconcileJournal(_stateStore, _healthCredentials ?? new HealthCredentials());
        }

        var applianceState = _stateStore.Read();

        // 1. Recovery journal that has not passed: stop with data-unusable
        if (applianceState.RecoveryJournal is { HealthPassed: false })
        {
            return SetupStepResult.Failed(FailureCode.DataUnusable,
                "A recovery operation was interrupted before completion. The Recover flow owns it.");
        }

        bool hasData = File.Exists(Data);
        var readiness = applianceState.Readiness;

        // 2. Data missing with readiness Initialized: stop with data-unusable; create nothing
        if (!hasData && readiness == ReadinessState.Initialized)
        {
            return SetupStepResult.Failed(FailureCode.DataUnusable,
                "Company data is missing while appliance is recorded as initialized.");
        }

        // 3. Decide route once before Build (KTD11)
        var currentRoute = _setupStore?.Read().CompanyDataRoute ?? CompanyDataRoute.Undecided;
        if (currentRoute == CompanyDataRoute.Undecided)
        {
            var route = DecideRoute(hasData, readiness);
            _setupStore?.Mutate(s => s.CompanyDataRoute = route);
            context.Log($"Company data route decided: {route}.");
        }

        return null;
    }

    public static CompanyDataRoute DecideRoute(bool hasData, ReadinessState readiness)
    {
        if (!hasData) return CompanyDataRoute.CreateNew;
        if (readiness != ReadinessState.Initialized) return CompanyDataRoute.AdoptExisting;
        return CompanyDataRoute.Undecided;
    }

    /// <summary>
    /// Returns the extra disk bytes required for adoption working copy if route is AdoptExisting (KTD11).
    /// Used by the PC check free-space evaluator.
    /// </summary>
    public long GetExtraDiskBytes() =>
        GetExtraDiskBytes(_applianceDir(), _setupStore, _stateStore);

    public static long GetExtraDiskBytes(string applianceDir, SetupStateStore? setupStore, StateStore? stateStore)
    {
        var route = setupStore?.Read().CompanyDataRoute ?? CompanyDataRoute.Undecided;
        string dataPath = Path.Combine(applianceDir, "data.img");

        if (route == CompanyDataRoute.Undecided)
        {
            bool hasData = File.Exists(dataPath);
            bool initialized = stateStore?.Read().Readiness == ReadinessState.Initialized;
            route = DecideRoute(hasData, initialized ? ReadinessState.Initialized : ReadinessState.NotBuilt);
        }

        if (route == CompanyDataRoute.AdoptExisting && File.Exists(dataPath))
        {
            var info = new FileInfo(dataPath);
            return info.Length + (2L * 1024 * 1024 * 1024);
        }

        return 0L;
    }

    public async Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        var preflight = Preflight(context);
        if (preflight is { Outcome: OperationOutcome.Failure })
            return preflight;

        var route = _setupStore?.Read().CompanyDataRoute ?? CompanyDataRoute.Undecided;
        if (route == CompanyDataRoute.Undecided)
        {
            route = DecideRoute(File.Exists(Data), _stateStore.Read().Readiness);
            _setupStore?.Mutate(s => s.CompanyDataRoute = route);
        }

        if (route == CompanyDataRoute.AdoptExisting)
        {
            if (_adopt is null && adoptWork is null)
                return SetupStepResult.Failed(FailureCode.DataUnusable,
                    "Company data already exists but is not recorded as ready. Setup does not replace it.");
            var creds = _healthCredentials?.Retrieve();
            string password = creds?.AdminPassword ?? DefaultPassword;

            var parameters = new AdoptParameters(Data, password);
            if (adoptWork is not null)
            {
                var workResult = await adoptWork(parameters, true, context.AsProgress(), ct);
                return SetupStepResult.From(workResult);
            }

            ArchiveInspection inspection;
            if (_inspectArchive is not null)
            {
                inspection = await _inspectArchive.ExecuteAsync(Data, ct);
            }
            else
            {
                inspection = new ArchiveInspection(ArchiveInspectionVerdict.AuthenticatedPreflightPassed, Data);
            }

            var result = await _adopt!.ExecuteAsync(
                Guid.NewGuid(),
                parameters,
                inspection,
                legacyConsentApproved: true,
                context.AsProgress(),
                ct);

            return SetupStepResult.From(result);
        }
        else
        {
            // CreateNew route
            if (File.Exists(Data))
                return SetupStepResult.Failed(FailureCode.DataUnusable,
                    "Company data already exists but is not recorded as ready. Setup does not replace it.");
            if (initializeWork is not null)
            {
                var workResult = await initializeWork(SiteName, DefaultPassword, context.AsProgress(), ct);
                return SetupStepResult.From(workResult);
            }

            var result = await _initialize.ExecuteAsync(
                Guid.NewGuid(), SiteName, DefaultPassword, context.AsProgress(), ct);
            return SetupStepResult.From(result);
        }
    }

    public void DeleteLeftovers(SetupContext context)
    {
        if (context.JournalOpen) return;

        // A pending disk that never became data.img is not startable (KTD8 of the lifecycle plan).
        foreach (var path in new[] { Pending, PendingAdopted })
        {
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
                context.Log($"Deleted the leftover {Path.GetFileName(path)}.");
            }
            catch (IOException ex)
            {
                context.Log($"Could not delete the leftover {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }
}
