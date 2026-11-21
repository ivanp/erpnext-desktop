using Serpy.Core.Contracts;
using Serpy.Core.Downloads;

namespace Serpy.Core.Setup;

/// <summary>
/// Downloads and verifies every listed build input except the engine installer (Prepare downloads that).
/// Parallel within limits, reports per-part progress, and never re-downloads a verified file (KTD27, KTD28).
/// </summary>
public sealed class DownloadStep(DownloadEngine engine, DownloadCache cache) : ISetupStep
{
    private const string EnginePartId = "engine";

    public SetupStepId Id => SetupStepId.Download;
    public int Weight => 15;
    public TimeSpan QuietTime => TimeSpan.FromMinutes(2);

    private IReadOnlyList<string> NonInstallerPartIds =>
        engine.Inputs.Parts
            .Where(p => !string.Equals(p.Id, EnginePartId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToList();

    public bool IsDone(SetupContext context)
    {
        var nonInstallerParts = engine.Inputs.Parts
            .Where(p => !string.Equals(p.Id, EnginePartId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nonInstallerParts.Count == 0) return true;

        foreach (var part in nonInstallerParts)
        {
            foreach (var file in part.Files)
            {
                if (!cache.TryVerify(file))
                    return false;
            }
        }

        return true;
    }

    public async Task<SetupStepResult> RunAsync(SetupContext context, CancellationToken ct)
    {
        context.Activity("Downloading what ERPNext needs");
        context.Log("Starting download step for non-installer parts.");

        var nonInstallerParts = NonInstallerPartIds;
        context.DownloadPartsUpdated?.Invoke(engine.GetSnapshots());

        var progress = new Progress<PartDownloadSnapshot>(snap =>
        {
            var snapshots = engine.GetSnapshots();
            context.DownloadPartsUpdated?.Invoke(snapshots);

            var totalBytes = snapshots.Where(s => !string.Equals(s.PartId, EnginePartId, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Total);
            var downloadedBytes = snapshots.Where(s => !string.Equals(s.PartId, EnginePartId, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Bytes);

            var pct = totalBytes > 0 ? (int)(downloadedBytes * 100 / totalBytes) : 0;
            context.StepPercent(Math.Clamp(pct, 0, 100));

            if (snap.State == PartDownloadState.Downloading)
            {
                context.Activity($"Downloading {snap.PartId}…");
            }
            else if (snap.State == PartDownloadState.Checking)
            {
                context.Activity($"Checking {snap.PartId}…");
            }
        });

        try
        {
            await engine.DownloadPartsAsync(nonInstallerParts, progress, ct).ConfigureAwait(false);
            context.DownloadPartsUpdated?.Invoke(engine.GetSnapshots());
            return SetupStepResult.Success("All build inputs downloaded and verified.");
        }
        catch (DownloadException ex)
        {
            context.Log($"DownloadStep failed with code {ex.Code}: {ex.Message}");
            return SetupStepResult.Failed(ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Log($"DownloadStep failed: {ex.Message}");
            return SetupStepResult.Failed(FailureCode.ProvisioningFailed, ex.Message);
        }
    }

    public void DeleteLeftovers(SetupContext context)
    {
        cache.DeleteLeftovers();
    }
}
