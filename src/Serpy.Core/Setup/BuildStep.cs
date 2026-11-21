using Serpy.Core.Configuration;
using Serpy.Core.Contracts;
using Serpy.Core.Coordination;
using Serpy.Core.Images;
using Serpy.Core.Operations;

namespace Serpy.Core.Setup;

/// <summary>
/// Builds <c>system.qcow2</c> (KTD7). It is skipped when an accepted system image already exists.
/// An accepted image with appliance state saying "not built" is recorded as built, and no new build
/// runs (the high-level design note under the company data routing table).
/// </summary>
public sealed class BuildStep(
    BuildOperation build,
    StateStore stateStore,
    Func<string>? applianceDir = null) : ISetupStep
{
    private readonly Func<string> _applianceDir = applianceDir ?? (() => KnownPaths.ApplianceDir);

    public SetupStepId Id => SetupStepId.Build;
    public int Weight => 55;
    public TimeSpan QuietTime => TimeSpan.FromMinutes(10);

    private string SystemImage => Path.Combine(_applianceDir(), "system.qcow2");
    private string Staging => Path.Combine(_applianceDir(), ".staging-system.qcow2");
    private string Seed => Path.Combine(_applianceDir(), ".cloud-init-seed.iso");

    public bool IsDone(SetupContext context)
    {
        if (!SystemImageManifest.IsAccepted(SystemImage)) return false;

        // The image is good. Make appliance state agree with the files, so later steps see it.
        if (stateStore.Read().Readiness == ReadinessState.NotBuilt)
        {
            context.Log("An accepted system image exists but appliance state said not built; recording it.");
            BuildOperation.RecordAcceptedBuild(stateStore, SystemImage);
        }

        return true;
    }

    public async Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        var result = await build.ExecuteAsync(Guid.NewGuid(), context.AsProgress(), ct);
        return SetupStepResult.From(result);
    }

    public void DeleteLeftovers(SetupContext context)
    {
        // An open journal owns its pending files; Build owns none of them, but the rule is general.
        if (context.JournalOpen) return;

        DeleteIfPresent(Staging, context);
        DeleteIfPresent(Seed, context);
    }

    private static void DeleteIfPresent(string path, SetupContext context)
    {
        if (!File.Exists(path)) return;
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
