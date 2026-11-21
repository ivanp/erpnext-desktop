using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Serpy.Core.Configuration;

namespace Serpy.App.Platform.Windows;

public enum SingleInstanceCommand
{
    Show,
    ShowSetup,
    OpenNotification,
}

/// <summary>
/// Inter-process communication between multiple Serpy.App launches via a per-user named pipe (KTD20).
/// Only a fixed list of UI commands is accepted; administrative or unattended commands are rejected.
/// </summary>
public static class SingleInstanceChannel
{
    public const string AppUserModelId = "Serpy.App";

    public static string ResolvePipeName(string? appDataRoot = null)
    {
        var appData = appDataRoot ?? KnownPaths.AppDataRoot;
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(appData)));
        return $"Serpy-Pipe-{Environment.UserName}-{hash[..8]}";
    }

    public static SingleInstanceCommand? ParseCommand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();

        if (string.Equals(trimmed, "Show", StringComparison.OrdinalIgnoreCase))
            return SingleInstanceCommand.Show;

        if (string.Equals(trimmed, "ShowSetup", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "--resume-setup", StringComparison.OrdinalIgnoreCase))
            return SingleInstanceCommand.ShowSetup;

        if (string.Equals(trimmed, "OpenNotification", StringComparison.OrdinalIgnoreCase))
            return SingleInstanceCommand.OpenNotification;

        return null;
    }

    public static IDisposable StartServer(string pipeName, Action<SingleInstanceCommand> onCommand, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);

                    if (ParseCommand(line) is { } cmd)
                    {
                        onCommand(cmd);
                    }
                }
                catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Best-effort channel loop; continue to next client connection
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
                }
            }
        }, cts.Token);

        return new ServerSubscription(cts);
    }

    public static async Task<bool> SendCommandAsync(string pipeName, SingleInstanceCommand command, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            await using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            await writer.WriteLineAsync(command.ToString().AsMemory(), cts.Token).ConfigureAwait(false);

            if (OperatingSystem.IsWindows())
            {
                // Allow the target server process to bring its window to the foreground
                AllowSetForegroundWindow(ASFW_ANY);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAppUserModelId()
    {
        if (OperatingSystem.IsWindows())
        {
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }
        }
    }

    private const int ASFW_ANY = -1;

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [SupportedOSPlatform("windows")]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    private sealed class ServerSubscription(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
