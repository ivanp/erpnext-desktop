using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Operations;

namespace Serpy.Core.Setup;

/// <summary>
/// Creates the company data (KTD7, KTD10). This unit ships the create-new path only. Reusing
/// existing data (adoption in place) and the password handling arrive with U8.
/// Guided setup creates the site <c>site1.local</c>, the name that start and adoption assume
/// (KTD12), with the password <c>admin</c>.
/// </summary>
public sealed class CompanyDataStep(
    InitializeOperation initialize,
    StateStore stateStore,
    Func<string>? applianceDir = null) : ISetupStep
{
    public const string SiteName = "site1.local";

    // The password of new data. It is the documented default of KTD12 and is shown on the Ready
    // screen; the user changes it inside ERPNext.
    internal const string DefaultPassword = "admin";

    private readonly Func<string> _applianceDir = applianceDir ?? (() => KnownPaths.ApplianceDir);

    public SetupStepId Id => SetupStepId.CompanyData;
    public int Weight => 15;
    public TimeSpan QuietTime => TimeSpan.FromMinutes(10);

    private string Data => Path.Combine(_applianceDir(), "data.img");
    private string Pending => Path.Combine(_applianceDir(), ".pending-data.img");
    private string PendingAdopted => Path.Combine(_applianceDir(), ".pending-adopted-data.img");

    /// <summary>A committed data.img with readiness Initialized: the step is skipped.</summary>
    public bool IsDone(SetupContext context) =>
        File.Exists(Data) && stateStore.Read().Readiness == ReadinessState.Initialized;

    public async Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        // Existing data goes through adoption, which U8 adds. Until then it is never overwritten.
        if (File.Exists(Data))
            return SetupStepResult.Failed(FailureCode.DataUnusable,
                "Company data already exists but is not recorded as ready. Setup does not replace it.");

        var result = await initialize.ExecuteAsync(
            Guid.NewGuid(), SiteName, DefaultPassword, context.AsProgress(), ct);
        return SetupStepResult.From(result);
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
