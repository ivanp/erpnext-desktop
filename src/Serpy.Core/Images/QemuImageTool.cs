using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Serpy.Core.Images;

/// <summary>
/// Managed wrapper around qemu-img.
/// All image management calls go through here — no ad-hoc process spawning.
/// </summary>
public class QemuImageTool(string qemuImgExe)
{
    /// <summary>
    /// Create a copy of <paramref name="sourcePath"/> as <paramref name="destPath"/> in qcow2 format.
    /// Used to make a working copy of the base Debian image.
    /// </summary>
    public virtual async Task CreateCopyAsync(
        string sourcePath,
        string destPath,
        CancellationToken ct = default) =>
        await RunAsync(["create", "-f", "qcow2", "-b", sourcePath, "-F", "qcow2", destPath], ct);

    /// <summary>
    /// Commits a temporary qcow2 overlay into its backing image. The caller uses this only after
    /// the guest has passed its health gate and shut down cleanly; failed runs delete the overlay
    /// instead, preserving the accepted system image.
    /// </summary>
    public virtual async Task CommitAsync(string overlayPath, CancellationToken ct = default) =>
        await RunAsync(["commit", overlayPath], ct);

    /// <summary>
    /// Create a fresh sparse RAW image of <paramref name="sizeGb"/> GiB.
    /// Used for data.img creation.
    /// </summary>
    public virtual async Task CreateSparseRawAsync(
        string destPath,
        int sizeGb,
        CancellationToken ct = default) =>
        await RunAsync(["create", "-f", "raw", destPath, $"{sizeGb}G"], ct);

    /// <summary>
    /// Resize an image to <paramref name="sizeGb"/> GiB.
    /// </summary>
    public virtual async Task ResizeAsync(
        string imagePath,
        int sizeGb,
        CancellationToken ct = default) =>
        await RunAsync(["resize", imagePath, $"{sizeGb}G"], ct);

    /// <summary>
    /// Return the backing_file field from qcow2 info, or null if not set.
    /// Used by recover to reject images with an arbitrary backing chain.
    /// </summary>
    public virtual async Task<string?> GetBackingFileAsync(
        string imagePath,
        CancellationToken ct = default)
    {
        var output = await RunAsync(["info", "--output=text", imagePath], ct, captureOutput: true);
        var match = Regex.Match(output, @"^backing file:\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<string> RunAsync(
        string[] args,
        CancellationToken ct,
        bool captureOutput = false)
    {
        var psi = new ProcessStartInfo(qemuImgExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stderr = proc.StandardError.ReadToEndAsync(ct);
        var stdout = captureOutput ? proc.StandardOutput.ReadToEndAsync(ct) : Task.FromResult(string.Empty);
        await Task.WhenAll(stderr, stdout);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var errText = await stderr;
            throw new InvalidOperationException(
                $"qemu-img {string.Join(' ', args)} failed (exit {proc.ExitCode}): {errText}");
        }

        return captureOutput ? await stdout : string.Empty;
    }
}
