using System.Text.Json;

namespace Serpy.Core.Configuration;

/// <summary>
/// Atomic JSON store for host <see cref="SetupState"/>: write-to-temp-then-rename,
/// never corrupting the live file. Source-generated serialization keeps it
/// Native-AOT compatible.
///
/// Separate from the appliance lifecycle state store on purpose (see
/// <see cref="SetupState"/>); it holds only setup markers.
/// </summary>
public sealed class SetupStateStore
{
    private readonly string _path;

    /// <summary>Raised when a state file had to be discarded, so that the event reaches a log.</summary>
    public event Action<string>? Problem;

    /// <param name="path">State file. Defaults to the settings folder.</param>
    public SetupStateStore(string? path = null)
    {
        _path = path ?? Path.Combine(KnownPaths.SettingsDir, SetupState.FileName);
    }

    /// <summary>
    /// Read current setup state. A schema 1 file loads as schema 2 with defaults and keeps its
    /// restart flag. A missing file, a corrupt file or a file of any other schema yields a fresh
    /// default rather than throwing; the last two are reported to the problem callback.
    /// </summary>
    public SetupState Read()
    {
        if (!File.Exists(_path))
            return new SetupState();

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize(json, SetupStateJsonContext.Default.SetupState);
            if (state is null)
            {
                Problem?.Invoke($"Setup state file {_path} is empty; starting from a fresh state.");
                return new SetupState();
            }

            if (state.SchemaVersion == 1)
            {
                // Schema 1 held the restart flag only. Every field added since has its default.
                state.SchemaVersion = SetupState.CurrentSchemaVersion;
                return state;
            }

            if (state.SchemaVersion != SetupState.CurrentSchemaVersion)
            {
                Problem?.Invoke(
                    $"Setup state file {_path} has schema {state.SchemaVersion}; starting from a fresh state.");
                return new SetupState();
            }

            state.Steps ??= [];
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Problem?.Invoke($"Setup state file {_path} could not be read ({ex.Message}); starting from a fresh state.");
            return new SetupState();
        }
    }

    /// <summary>Atomically write state via a sibling .tmp then rename over.</summary>
    public void Write(SetupState state)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(state, SetupStateJsonContext.Default.SetupState);
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Read → mutate → write atomically; returns the written state.</summary>
    public SetupState Mutate(Action<SetupState> mutate)
    {
        var state = Read();
        mutate(state);
        Write(state);
        return state;
    }
}
